using CivicOps.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CivicOps.Web.Services;

public sealed record CategoryAdminRow(int Id, string Name, string Department, bool IsActive, int RequestCount,
    IReadOnlyDictionary<RequestPriority, int> SlaHours);

public sealed record DistrictAdminRow(int Id, string Code, string Name, int Population, int RequestCount);

public sealed record AuditRow(int Id, DateTime At, string Actor, string Action, string EntityType, string EntityKey, string? Details);

/// <summary>Categories, SLA rules and districts (Admin only), the audit log and the demo-data reset.</summary>
public sealed class AdminService(IDbContextFactory<CivicOpsDb> factory, DemoDataSeeder seeder, TimeProvider clock)
{
    public const int MinSlaHours = 1;
    public const int MaxSlaHours = 24 * 90;

    public async Task<IReadOnlyList<CategoryAdminRow>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var counts = await db.Requests.GroupBy(r => r.CategoryId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var cats = await db.Categories.AsNoTracking().Include(c => c.SlaRules).OrderBy(c => c.Name).ToListAsync(ct);
        return cats.Select(c => new CategoryAdminRow(c.Id, c.Name, c.Department, c.IsActive, counts.GetValueOrDefault(c.Id),
            c.SlaRules.ToDictionary(s => s.Priority, s => s.TargetHours))).ToList();
    }

    public async Task<OpResult> SaveCategoryAsync(Actor? actor, int? id, string? name, string? department, bool isActive,
        IReadOnlyDictionary<RequestPriority, int> slaHours, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanAdminister) return OpResult.Fail(Failure.Forbidden, "Only admins can change categories and SLA rules.");
        name = name?.Trim();
        department = department?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 80) return OpResult.Fail(Failure.Invalid, "Name is required (80 characters max).");
        if (string.IsNullOrEmpty(department) || department.Length > 80) return OpResult.Fail(Failure.Invalid, "Department is required (80 characters max).");
        foreach (var p in Enum.GetValues<RequestPriority>())
            if (!slaHours.TryGetValue(p, out var h) || h < MinSlaHours || h > MaxSlaHours)
                return OpResult.Fail(Failure.Invalid, $"SLA hours for {p} must be between {MinSlaHours} and {MaxSlaHours}.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Categories.AnyAsync(c => c.Name == name && c.Id != (id ?? 0), ct))
            return OpResult.Fail(Failure.Invalid, "A category with that name already exists.");

        Category cat;
        if (id is null)
        {
            cat = new Category();
            db.Categories.Add(cat);
        }
        else
        {
            var found = await db.Categories.Include(c => c.SlaRules).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (found is null) return OpResult.Fail(Failure.NotFound, "Category not found.");
            cat = found;
        }
        cat.Name = name;
        cat.Department = department;
        cat.IsActive = isActive;
        foreach (var (priority, hours) in slaHours)
        {
            var rule = cat.SlaRules.FirstOrDefault(s => s.Priority == priority);
            if (rule is null) cat.SlaRules.Add(new SlaRule { Priority = priority, TargetHours = hours });
            else rule.TargetHours = hours;
        }
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }

    /// <summary>Hard delete, only when nothing references the category; otherwise the admin should deactivate it.</summary>
    public async Task<OpResult> DeleteCategoryAsync(Actor? actor, int id, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanAdminister) return OpResult.Fail(Failure.Forbidden, "Only admins can delete categories.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cat is null) return OpResult.Fail(Failure.NotFound, "Category not found.");
        var used = await db.Requests.CountAsync(r => r.CategoryId == id, ct);
        if (used > 0)
            return OpResult.Fail(Failure.Invalid, $"{cat.Name} is used by {used} request{(used == 1 ? "" : "s")} and cannot be deleted. Deactivate it instead so it is hidden from new requests.");
        Audit.Add(db, actor, clock.GetUtcNow().UtcDateTime, "Category deleted", "Category", cat.Name, $"Department: {cat.Department}");
        db.Categories.Remove(cat); // SLA rules cascade
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }

    public async Task<IReadOnlyList<DistrictAdminRow>> GetDistrictsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var counts = await db.Requests.GroupBy(r => r.DistrictId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var list = await db.Districts.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return list.Select(d => new DistrictAdminRow(d.Id, d.Code, d.Name, d.Population, counts.GetValueOrDefault(d.Id))).ToList();
    }

    public async Task<OpResult> SaveDistrictAsync(Actor? actor, int? id, string? code, string? name, int population, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanAdminister) return OpResult.Fail(Failure.Forbidden, "Only admins can change districts.");
        code = code?.Trim().ToUpperInvariant();
        name = name?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 8) return OpResult.Fail(Failure.Invalid, "Code is required (8 characters max).");
        if (string.IsNullOrEmpty(name) || name.Length > 60) return OpResult.Fail(Failure.Invalid, "Name is required (60 characters max).");
        if (population < 0 || population > 10_000_000) return OpResult.Fail(Failure.Invalid, "Population must be between 0 and 10,000,000.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Districts.AnyAsync(d => (d.Code == code || d.Name == name) && d.Id != (id ?? 0), ct))
            return OpResult.Fail(Failure.Invalid, "A district with that code or name already exists.");
        District d;
        if (id is null) { d = new District(); db.Districts.Add(d); }
        else
        {
            var found = await db.Districts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (found is null) return OpResult.Fail(Failure.NotFound, "District not found.");
            d = found;
        }
        d.Code = code; d.Name = name; d.Population = population;
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }

    public async Task<OpResult> DeleteDistrictAsync(Actor? actor, int id, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanAdminister) return OpResult.Fail(Failure.Forbidden, "Only admins can delete districts.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var d = await db.Districts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return OpResult.Fail(Failure.NotFound, "District not found.");
        var used = await db.Requests.CountAsync(r => r.DistrictId == id, ct);
        if (used > 0)
            return OpResult.Fail(Failure.Invalid, $"{d.Name} is used by {used} request{(used == 1 ? "" : "s")} and cannot be deleted.");
        Audit.Add(db, actor, clock.GetUtcNow().UtcDateTime, "District deleted", "District", d.Code, d.Name);
        db.Districts.Remove(d);
        await db.SaveChangesAsync(ct);
        return OpResult.Success;
    }

    public async Task<IReadOnlyList<AuditRow>> GetAuditAsync(Actor? actor, int take = 100, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanAdminister) return [];
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(take)
            .Select(a => new AuditRow(a.Id, a.At, a.ActorName, a.Action, a.EntityType, a.EntityKey, a.Details)).ToListAsync(ct);
    }

    public async Task<OpResult> ResetDemoDataAsync(Actor? actor, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanAdminister) return OpResult.Fail(Failure.Forbidden, "Only admins can reset the demo data.");
        await seeder.ResetAsync(ct);
        return OpResult.Success;
    }
}
