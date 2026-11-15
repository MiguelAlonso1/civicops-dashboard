using CivicOps.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CivicOps.Web.Services;

public sealed record CategoryAdminRow(int Id, string Name, string Department, bool IsActive, int RequestCount,
    IReadOnlyDictionary<RequestPriority, int> SlaHours);

/// <summary>Categories and SLA rules (Admin only) plus the demo-data reset.</summary>
public sealed class AdminService(IDbContextFactory<CivicOpsDb> factory, DemoDataSeeder seeder)
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

    public async Task<OpResult> ResetDemoDataAsync(Actor? actor, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanAdminister) return OpResult.Fail(Failure.Forbidden, "Only admins can reset the demo data.");
        await seeder.ResetAsync(ct);
        return OpResult.Success;
    }
}
