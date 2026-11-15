using CivicOps.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CivicOps.Web.Services;

public sealed record LabeledValue(string Label, double Value);

public sealed record KpiSet(int Open, int Overdue, double AvgResolutionHours, double SlaCompliancePct, int Total, int OpenedLast7Days, int ResolvedLast7Days);

public sealed record DistrictRow(int Id, string Code, string Name, int Population, int Total, int Open, int Overdue,
    double AvgResolutionHours, double SlaCompliancePct, double PerTenThousand);

public sealed record DashboardData(
    KpiSet Kpis,
    IReadOnlyList<LabeledValue> CreatedPerWeek,
    IReadOnlyList<LabeledValue> ResolvedPerWeek,
    IReadOnlyList<LabeledValue> ByCategory,
    IReadOnlyList<LabeledValue> ByDistrict,
    IReadOnlyList<LabeledValue> SlaTrend,
    IReadOnlyList<DistrictRow> Districts);

/// <summary>Aggregates for the overview and district pages. Computed in memory from a narrow projection (a few thousand rows at most).</summary>
public sealed class DashboardService(IDbContextFactory<CivicOpsDb> factory, TimeProvider clock)
{
    private sealed record Row(int CategoryId, int DistrictId, RequestStatus Status, DateTime CreatedAt, DateTime DueAt, DateTime? ResolvedAt);

    public async Task<DashboardData> GetAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await db.Requests.AsNoTracking()
            .Select(r => new Row(r.CategoryId, r.DistrictId, r.Status, r.CreatedAt, r.DueAt, r.ResolvedAt)).ToListAsync(ct);
        var cats = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var dists = await db.Districts.AsNoTracking().OrderBy(d => d.Code).ToListAsync(ct);

        return new DashboardData(
            Kpis(rows, now),
            WeeklySeries(rows, now, r => r.CreatedAt),
            WeeklySeries(rows.Where(r => r.ResolvedAt != null).ToList(), now, r => r.ResolvedAt!.Value),
            rows.GroupBy(r => r.CategoryId).Select(g => new LabeledValue(cats[g.Key], g.Count())).OrderByDescending(x => x.Value).ToList(),
            rows.GroupBy(r => r.DistrictId).Select(g => new LabeledValue(dists.First(d => d.Id == g.Key).Name, g.Count())).OrderByDescending(x => x.Value).ToList(),
            SlaTrend(rows, now),
            dists.Select(d => District(d, rows.Where(r => r.DistrictId == d.Id).ToList(), now)).ToList());
    }

    private static bool IsOpen(Row r) => StatusRules.IsOpen(r.Status);

    /// <summary>A request meets its SLA if resolved by its due date; open requests count against SLA once past due.</summary>
    private static bool? Met(Row r, DateTime now)
    {
        if (r.Status == RequestStatus.Canceled) return null;
        if (r.ResolvedAt is { } at) return at <= r.DueAt;
        return r.DueAt < now ? false : null; // still open and inside its window: not yet decided
    }

    private static double Pct(IEnumerable<Row> rows, DateTime now)
    {
        var decided = rows.Select(r => Met(r, now)).Where(m => m is not null).ToList();
        return decided.Count == 0 ? 100 : 100.0 * decided.Count(m => m == true) / decided.Count;
    }

    private static double AvgHours(IEnumerable<Row> rows)
    {
        var done = rows.Where(r => r.ResolvedAt is not null).Select(r => (r.ResolvedAt!.Value - r.CreatedAt).TotalHours).ToList();
        return done.Count == 0 ? 0 : done.Average();
    }

    private static KpiSet Kpis(List<Row> rows, DateTime now)
    {
        var recent = rows.Where(r => r.CreatedAt >= now.AddDays(-90)).ToList();
        return new KpiSet(
            rows.Count(IsOpen),
            rows.Count(r => IsOpen(r) && r.DueAt < now),
            AvgHours(recent),
            Pct(recent, now),
            rows.Count,
            rows.Count(r => r.CreatedAt >= now.AddDays(-7)),
            rows.Count(r => r.ResolvedAt >= now.AddDays(-7)));
    }

    private static List<LabeledValue> WeeklySeries(List<Row> rows, DateTime now, Func<Row, DateTime> date, int weeks = 12)
    {
        var result = new List<LabeledValue>();
        for (var i = weeks - 1; i >= 0; i--)
        {
            var end = now.AddDays(-7 * i);
            var start = end.AddDays(-7);
            result.Add(new LabeledValue(end.ToString("MMM d"), rows.Count(r => date(r) > start && date(r) <= end)));
        }
        return result;
    }

    private static List<LabeledValue> SlaTrend(List<Row> rows, DateTime now)
    {
        var result = new List<LabeledValue>();
        for (var i = 11; i >= 0; i--)
        {
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-i);
            var end = start.AddMonths(1);
            var inMonth = rows.Where(r => r.CreatedAt >= start && r.CreatedAt < end).ToList();
            result.Add(new LabeledValue(start.ToString("MMM"), Math.Round(Pct(inMonth, now), 1)));
        }
        return result;
    }

    private static DistrictRow District(District d, List<Row> rows, DateTime now) =>
        new(d.Id, d.Code, d.Name, d.Population, rows.Count, rows.Count(IsOpen), rows.Count(r => IsOpen(r) && r.DueAt < now),
            AvgHours(rows), Pct(rows, now), d.Population == 0 ? 0 : rows.Count * 10000.0 / d.Population);
}
