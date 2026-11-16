using CivicOps.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CivicOps.Web.Services;

public sealed record RequestView(
    int Id, string Number, string Title, string Description, string Address, string RequesterName,
    int CategoryId, string Category, string Department, string District, RequestPriority Priority, RequestStatus Status,
    string? AssigneeId, string? Assignee, DateTime CreatedAt, DateTime DueAt, DateTime? ResolvedAt,
    IReadOnlyList<RequestEvent> Events, int DistrictId = 0);

public sealed record AssigneeOption(string Id, string Name);

/// <summary>Editable core fields of a request (create and edit share the same validation).</summary>
public sealed record RequestInput(string? Title, string? Description, int CategoryId, int DistrictId, RequestPriority Priority,
    string? Address, string? RequesterName);

public sealed record CategoryOption(int Id, string Name, string Department, bool IsActive, IReadOnlyDictionary<RequestPriority, int> SlaHours);

public sealed record DistrictOption(int Id, string Code, string Name);

public sealed record RequestLookups(IReadOnlyList<CategoryOption> Categories, IReadOnlyList<DistrictOption> Districts);

public readonly record struct CreateResult(OpResult Result, int? Id)
{
    public bool Ok => Result.Ok;
}

/// <summary>Reads one request and applies the workflow commands. Every command re-checks permissions server-side.</summary>
public sealed class RequestCommandService(IDbContextFactory<CivicOpsDb> factory, UserManager<ApplicationUser> users, TimeProvider clock)
{
    public const int MaxNoteLength = 1000;

    public async Task<RequestView?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Requests.AsNoTracking()
            .Include(x => x.Category).Include(x => x.District).Include(x => x.Assignee).Include(x => x.Events)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return null;
        return new RequestView(r.Id, r.Number, r.Title, r.Description, r.Address, r.RequesterName, r.CategoryId,
            r.Category!.Name, r.Category.Department, r.District!.Name, r.Priority, r.Status, r.AssigneeId,
            r.Assignee?.DisplayName, r.CreatedAt, r.DueAt, r.ResolvedAt,
            r.Events.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).ToList(), r.DistrictId);
    }

    public async Task<IReadOnlyList<AssigneeOption>> GetAssigneesAsync(CancellationToken ct = default)
    {
        var list = new List<AssigneeOption>();
        foreach (var role in new[] { Roles.Agent, Roles.Supervisor })
            foreach (var u in await users.GetUsersInRoleAsync(role))
                list.Add(new AssigneeOption(u.Id, u.DisplayName));
        return list.OrderBy(a => a.Name).ToList();
    }

    public async Task<OpResult> ChangeStatusAsync(Actor? actor, int id, RequestStatus to, string? note, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanEdit) return OpResult.Fail(Failure.Forbidden, "Your role cannot update requests.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return OpResult.Fail(Failure.NotFound, "Request not found.");

        // Agents work their own queue; supervisors and admins can act on any request.
        if (actor.Role == Roles.Agent && r.AssigneeId != actor.UserId)
            return OpResult.Fail(Failure.Forbidden, "Agents can only update requests assigned to them.");

        if (!StatusRules.CanTransition(r.Status, to))
            return OpResult.Fail(Failure.Invalid, $"A request that is {StatusRules.Label(r.Status)} cannot move to {StatusRules.Label(to)}.");

        note = note?.Trim();
        if (StatusRules.RequiresNote(r.Status, to) && string.IsNullOrEmpty(note))
            return OpResult.Fail(Failure.Invalid, $"A note is required to move a request to {StatusRules.Label(to)}.");
        if (note is { Length: > MaxNoteLength })
            return OpResult.Fail(Failure.Invalid, $"Notes are limited to {MaxNoteLength} characters.");

        var now = clock.GetUtcNow().UtcDateTime;
        var from = r.Status;
        r.Status = to;
        r.UpdatedAt = now;
        if (to == RequestStatus.Resolved) r.ResolvedAt = now;
        else if (from == RequestStatus.Resolved && to == RequestStatus.InProgress) r.ResolvedAt = null;
        db.Events.Add(new RequestEvent
        {
            ServiceRequestId = r.Id, At = now, Kind = EventKind.StatusChanged, FromStatus = from, ToStatus = to,
            Actor = actor.Name, ActorId = actor.UserId, Note = string.IsNullOrEmpty(note) ? null : note
        });
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }

    public async Task<OpResult> AssignAsync(Actor? actor, int id, string? assigneeId, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanAssign) return OpResult.Fail(Failure.Forbidden, "Only supervisors and admins can assign requests.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return OpResult.Fail(Failure.NotFound, "Request not found.");
        if (!StatusRules.IsOpen(r.Status)) return OpResult.Fail(Failure.Invalid, "Closed, resolved or canceled requests cannot be reassigned.");

        string? name = null;
        if (!string.IsNullOrEmpty(assigneeId))
        {
            var u = await users.FindByIdAsync(assigneeId);
            if (u is null || !(await users.IsInRoleAsync(u, Roles.Agent) || await users.IsInRoleAsync(u, Roles.Supervisor)))
                return OpResult.Fail(Failure.Invalid, "That user cannot be assigned requests.");
            name = u.DisplayName;
        }
        if (r.AssigneeId == assigneeId) return OpResult.Success;

        var now = clock.GetUtcNow().UtcDateTime;
        r.AssigneeId = string.IsNullOrEmpty(assigneeId) ? null : assigneeId;
        r.UpdatedAt = now;
        db.Events.Add(new RequestEvent
        {
            ServiceRequestId = r.Id, At = now, Kind = EventKind.Assigned, Actor = actor.Name, ActorId = actor.UserId,
            Note = name is null ? "Unassigned" : $"Assigned to {name}"
        });
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }

    public async Task<OpResult> AddNoteAsync(Actor? actor, int id, string? text, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanEdit) return OpResult.Fail(Failure.Forbidden, "Your role cannot add notes.");
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return OpResult.Fail(Failure.Invalid, "Write a note first.");
        if (text.Length > MaxNoteLength) return OpResult.Fail(Failure.Invalid, $"Notes are limited to {MaxNoteLength} characters.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return OpResult.Fail(Failure.NotFound, "Request not found.");
        var now = clock.GetUtcNow().UtcDateTime;
        r.UpdatedAt = now;
        db.Events.Add(new RequestEvent { ServiceRequestId = r.Id, At = now, Kind = EventKind.Note, Actor = actor.Name, ActorId = actor.UserId, Note = text });
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }

    public async Task<RequestLookups> GetLookupsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cats = await db.Categories.AsNoTracking().Include(c => c.SlaRules).OrderBy(c => c.Name).ToListAsync(ct);
        var dists = await db.Districts.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return new RequestLookups(
            cats.Select(c => new CategoryOption(c.Id, c.Name, c.Department, c.IsActive, c.SlaRules.ToDictionary(x => x.Priority, x => x.TargetHours))).ToList(),
            dists.Select(d => new DistrictOption(d.Id, d.Code, d.Name)).ToList());
    }

    /// <summary>Shared field validation; returns the trimmed input or a failure.</summary>
    private static (RequestInput? Clean, OpResult Error) Validate(RequestInput i)
    {
        var title = i.Title?.Trim();
        var desc = i.Description?.Trim();
        var addr = i.Address?.Trim();
        var who = i.RequesterName?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 160) return (null, OpResult.Fail(Failure.Invalid, "Title is required (160 characters max)."));
        if (string.IsNullOrEmpty(desc) || desc.Length > 2000) return (null, OpResult.Fail(Failure.Invalid, "Description is required (2000 characters max)."));
        if (string.IsNullOrEmpty(addr) || addr.Length > 160) return (null, OpResult.Fail(Failure.Invalid, "Address is required (160 characters max)."));
        if (string.IsNullOrEmpty(who) || who.Length > 100) return (null, OpResult.Fail(Failure.Invalid, "Requester name is required (100 characters max)."));
        if (!Enum.IsDefined(i.Priority)) return (null, OpResult.Fail(Failure.Invalid, "Choose a valid priority."));
        return (i with { Title = title, Description = desc, Address = addr, RequesterName = who }, OpResult.Success);
    }

    private static async Task<(int? Hours, OpResult Error)> SlaAsync(CivicOpsDb db, RequestInput i, bool requireActiveCategory, CancellationToken ct)
    {
        var cat = await db.Categories.AsNoTracking().Include(c => c.SlaRules).FirstOrDefaultAsync(c => c.Id == i.CategoryId, ct);
        if (cat is null) return (null, OpResult.Fail(Failure.Invalid, "Choose a category."));
        if (requireActiveCategory && !cat.IsActive) return (null, OpResult.Fail(Failure.Invalid, "That category is inactive. Choose another one."));
        if (!await db.Districts.AnyAsync(d => d.Id == i.DistrictId, ct)) return (null, OpResult.Fail(Failure.Invalid, "Choose a district."));
        var rule = cat.SlaRules.FirstOrDefault(s => s.Priority == i.Priority);
        return rule is null ? (null, OpResult.Fail(Failure.Invalid, "The category has no SLA rule for that priority.")) : (rule.TargetHours, OpResult.Success);
    }

    public async Task<CreateResult> CreateAsync(Actor? actor, RequestInput input, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanCreateRequest) return new(OpResult.Fail(Failure.Forbidden, "Your role cannot create requests."), null);
        var (clean, err) = Validate(input);
        if (clean is null) return new(err, null);

        await using var db = await factory.CreateDbContextAsync(ct);
        var (hours, slaErr) = await SlaAsync(db, clean, requireActiveCategory: true, ct);
        if (hours is null) return new(slaErr, null);

        var now = clock.GetUtcNow().UtcDateTime;
        // Numbers continue the seeded sequence (SR-yyyy-nnnnn, the sequence is global).
        var last = await db.Requests.Where(r => r.Number.StartsWith("SR-")).OrderByDescending(r => r.Number).Select(r => r.Number).FirstOrDefaultAsync(ct);
        var seq = last is not null && int.TryParse(last[(last.LastIndexOf('-') + 1)..], out var n) ? n + 1 : 1;
        var r = new ServiceRequest
        {
            Number = $"SR-{now:yyyy}-{seq:00000}", Title = clean.Title!, Description = clean.Description!, Address = clean.Address!,
            RequesterName = clean.RequesterName!, CategoryId = clean.CategoryId, DistrictId = clean.DistrictId, Priority = clean.Priority,
            Status = RequestStatus.New, CreatedAt = now, DueAt = now.AddHours(hours.Value), UpdatedAt = now
        };
        r.Events.Add(new RequestEvent { At = now, Kind = EventKind.Created, Actor = actor.Name, ActorId = actor.UserId, Note = "Request created in CivicOps." });
        if (actor.Role == Roles.Agent)
        {
            // Agents can only work their own queue, so a request they log is assigned to them.
            r.AssigneeId = actor.UserId;
            r.Events.Add(new RequestEvent { At = now, Kind = EventKind.Assigned, Actor = actor.Name, ActorId = actor.UserId, Note = $"Assigned to {actor.Name}" });
        }
        db.Requests.Add(r);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return new(OpResult.Fail(Failure.Invalid, "Could not allocate a request number. Please try again."), null); }
        return new(OpResult.Success, r.Id);
    }

    public async Task<OpResult> EditAsync(Actor? actor, int id, RequestInput input, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanEdit) return OpResult.Fail(Failure.Forbidden, "Your role cannot edit requests.");
        var (clean, err) = Validate(input);
        if (clean is null) return err;

        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return OpResult.Fail(Failure.NotFound, "Request not found.");
        if (!actor.CanEditRequest(r.AssigneeId, r.Status))
            return OpResult.Fail(Failure.Forbidden, actor.Role == Roles.Agent
                ? "Agents can only edit open requests assigned to them." : "Your role cannot edit requests.");

        var categoryChanged = clean.CategoryId != r.CategoryId;
        var (hours, slaErr) = await SlaAsync(db, clean, requireActiveCategory: categoryChanged, ct);
        if (hours is null) return slaErr;

        var changes = new List<string>();
        if (r.Title != clean.Title) changes.Add("title");
        if (r.Description != clean.Description) changes.Add("description");
        if (r.Address != clean.Address) changes.Add("address");
        if (r.RequesterName != clean.RequesterName) changes.Add("requester");
        if (categoryChanged)
        {
            var oldCat = await db.Categories.AsNoTracking().FirstAsync(c => c.Id == r.CategoryId, ct);
            var newCat = await db.Categories.AsNoTracking().FirstAsync(c => c.Id == clean.CategoryId, ct);
            changes.Add($"category ({oldCat.Name} to {newCat.Name})");
        }
        if (clean.DistrictId != r.DistrictId)
        {
            var oldDist = await db.Districts.AsNoTracking().FirstAsync(d => d.Id == r.DistrictId, ct);
            var newDist = await db.Districts.AsNoTracking().FirstAsync(d => d.Id == clean.DistrictId, ct);
            changes.Add($"district ({oldDist.Name} to {newDist.Name})");
        }
        var priorityChanged = clean.Priority != r.Priority;
        if (priorityChanged) changes.Add($"priority ({r.Priority} to {clean.Priority})");
        if (changes.Count == 0) return OpResult.Success;

        var now = clock.GetUtcNow().UtcDateTime;
        r.Title = clean.Title!; r.Description = clean.Description!; r.Address = clean.Address!; r.RequesterName = clean.RequesterName!;
        r.CategoryId = clean.CategoryId; r.DistrictId = clean.DistrictId; r.Priority = clean.Priority;
        var dueChanged = false;
        if (categoryChanged || priorityChanged)
        {
            // The SLA target is recomputed from the original creation time with the new category/priority.
            var due = r.CreatedAt.AddHours(hours.Value);
            dueChanged = due != r.DueAt;
            r.DueAt = due;
        }
        r.UpdatedAt = now;
        var text = "Updated " + string.Join(", ", changes) + (dueChanged ? $". SLA due is now {r.DueAt:MMM d, yyyy HH:mm} UTC" : "") + ".";
        db.Events.Add(new RequestEvent
        {
            ServiceRequestId = r.Id, At = now, Kind = EventKind.Edited, Actor = actor.Name, ActorId = actor.UserId,
            Note = text.Length > MaxNoteLength ? text[..MaxNoteLength] : text
        });
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }

    /// <summary>Hard delete (Admin only): removes the request and its events and leaves an audit entry.</summary>
    public async Task<OpResult> DeleteAsync(Actor? actor, int id, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanDeleteRequest) return OpResult.Fail(Failure.Forbidden, "Only admins can delete requests.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Requests.Include(x => x.Category).Include(x => x.District).Include(x => x.Events).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return OpResult.Fail(Failure.NotFound, "Request not found.");
        var now = clock.GetUtcNow().UtcDateTime;
        Audit.Add(db, actor, now, "Request deleted", "ServiceRequest", r.Number,
            $"\"{r.Title}\" ({r.Category?.Name}, {r.District?.Name}, {r.Priority}, {StatusRules.Label(r.Status)}); {r.Events.Count} events removed.");
        db.Requests.Remove(r);
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }

    public async Task<OpResult> EditNoteAsync(Actor? actor, int eventId, string? text, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanEdit) return OpResult.Fail(Failure.Forbidden, "Your role cannot edit notes.");
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return OpResult.Fail(Failure.Invalid, "Write a note first.");
        if (text.Length > MaxNoteLength) return OpResult.Fail(Failure.Invalid, $"Notes are limited to {MaxNoteLength} characters.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var e = await db.Events.FirstOrDefaultAsync(x => x.Id == eventId && x.Kind == EventKind.Note, ct);
        if (e is null) return OpResult.Fail(Failure.NotFound, "Note not found. Status and system events cannot be edited.");
        if (!actor.CanModifyNote(e.ActorId)) return OpResult.Fail(Failure.Forbidden, "You can only edit your own notes.");
        e.Note = text;
        e.EditedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }

    public async Task<OpResult> DeleteNoteAsync(Actor? actor, int eventId, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanEdit) return OpResult.Fail(Failure.Forbidden, "Your role cannot delete notes.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var e = await db.Events.Include(x => x.ServiceRequest).FirstOrDefaultAsync(x => x.Id == eventId && x.Kind == EventKind.Note, ct);
        if (e is null) return OpResult.Fail(Failure.NotFound, "Note not found. Status and system events cannot be deleted.");
        if (!actor.CanModifyNote(e.ActorId)) return OpResult.Fail(Failure.Forbidden, "You can only delete your own notes.");
        var now = clock.GetUtcNow().UtcDateTime;
        var preview = e.Note is { Length: > 120 } ? e.Note[..120] + "..." : e.Note;
        Audit.Add(db, actor, now, "Note deleted", "RequestEvent", e.ServiceRequest?.Number ?? e.ServiceRequestId.ToString(), $"By {e.Actor}: {preview}");
        db.Events.Remove(e);
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }
}
