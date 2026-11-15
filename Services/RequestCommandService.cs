using CivicOps.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CivicOps.Web.Services;

public sealed record RequestView(
    int Id, string Number, string Title, string Description, string Address, string RequesterName,
    int CategoryId, string Category, string Department, string District, RequestPriority Priority, RequestStatus Status,
    string? AssigneeId, string? Assignee, DateTime CreatedAt, DateTime DueAt, DateTime? ResolvedAt,
    IReadOnlyList<RequestEvent> Events);

public sealed record AssigneeOption(string Id, string Name);

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
            r.Events.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).ToList());
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
            Actor = actor.Name, Note = string.IsNullOrEmpty(note) ? null : note
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
            ServiceRequestId = r.Id, At = now, Kind = EventKind.Assigned, Actor = actor.Name,
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
        db.Events.Add(new RequestEvent { ServiceRequestId = r.Id, At = now, Kind = EventKind.Note, Actor = actor.Name, Note = text });
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }
}
