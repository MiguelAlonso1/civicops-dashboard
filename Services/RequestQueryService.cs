using CivicOps.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CivicOps.Web.Services;

public sealed class RequestQueryService(IDbContextFactory<CivicOpsDb> factory, TimeProvider clock)
{
    public const int MaxPageSize = 100;
    public const int MaxExportRows = 5000;

    public async Task<PagedResult<RequestRow>> QueryAsync(RequestQuery q, Actor? actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var filtered = Apply(db.Requests.AsNoTracking(), q, actor);
        var total = await filtered.CountAsync(ct);

        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var lastPage = Math.Max(0, (total - 1) / size);
        var page = Math.Clamp(q.Page, 0, lastPage);

        var items = await Project(Sort(filtered, q)).Skip(page * size).Take(size).ToListAsync(ct);
        return new PagedResult<RequestRow>(items, total, page, size);
    }

    public async Task<IReadOnlyList<RequestRow>> ExportAsync(RequestQuery q, Actor? actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Project(Sort(Apply(db.Requests.AsNoTracking(), q, actor), q)).Take(MaxExportRows).ToListAsync(ct);
    }

    public IQueryable<ServiceRequest> Apply(IQueryable<ServiceRequest> src, RequestQuery q, Actor? actor)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            src = src.Where(r => EF.Functions.Like(r.Title, $"%{s}%") || EF.Functions.Like(r.Number, $"%{s}%")
                                 || EF.Functions.Like(r.Address, $"%{s}%"));
        }
        if (q.Status is { } st) src = src.Where(r => r.Status == st);
        if (q.CategoryId is { } c) src = src.Where(r => r.CategoryId == c);
        if (q.DistrictId is { } d) src = src.Where(r => r.DistrictId == d);
        if (q.Priority is { } p) src = src.Where(r => r.Priority == p);

        var openStatuses = new[] { RequestStatus.New, RequestStatus.InProgress, RequestStatus.OnHold };
        var userId = actor?.UserId;
        src = q.Quick switch
        {
            QuickFilter.Open => src.Where(r => openStatuses.Contains(r.Status)),
            QuickFilter.Overdue => src.Where(r => openStatuses.Contains(r.Status) && r.DueAt < now),
            QuickFilter.Unassigned => src.Where(r => openStatuses.Contains(r.Status) && r.AssigneeId == null),
            QuickFilter.Mine => userId is null ? src.Where(_ => false) : src.Where(r => r.AssigneeId == userId),
            QuickFilter.Urgent => src.Where(r => openStatuses.Contains(r.Status) && r.Priority >= RequestPriority.High),
            _ => src
        };
        return src;
    }

    private static IQueryable<ServiceRequest> Sort(IQueryable<ServiceRequest> src, RequestQuery q)
    {
        var desc = q.Descending;
        IOrderedQueryable<ServiceRequest> o = q.Sort switch
        {
            SortField.Number => desc ? src.OrderByDescending(r => r.Number) : src.OrderBy(r => r.Number),
            SortField.Title => desc ? src.OrderByDescending(r => r.Title) : src.OrderBy(r => r.Title),
            SortField.Category => desc ? src.OrderByDescending(r => r.Category!.Name) : src.OrderBy(r => r.Category!.Name),
            SortField.District => desc ? src.OrderByDescending(r => r.District!.Name) : src.OrderBy(r => r.District!.Name),
            SortField.Priority => desc ? src.OrderByDescending(r => r.Priority) : src.OrderBy(r => r.Priority),
            SortField.Status => desc ? src.OrderByDescending(r => r.Status) : src.OrderBy(r => r.Status),
            SortField.Assignee => desc ? src.OrderByDescending(r => r.Assignee!.DisplayName) : src.OrderBy(r => r.Assignee!.DisplayName),
            SortField.Due => desc ? src.OrderByDescending(r => r.DueAt) : src.OrderBy(r => r.DueAt),
            _ => desc ? src.OrderByDescending(r => r.CreatedAt) : src.OrderBy(r => r.CreatedAt),
        };
        return o.ThenBy(r => r.Id); // stable paging
    }

    private static IQueryable<RequestRow> Project(IQueryable<ServiceRequest> src) =>
        src.Select(r => new RequestRow(r.Id, r.Number, r.Title, r.Category!.Name, r.District!.Name, r.Priority, r.Status,
            r.Assignee != null ? r.Assignee.DisplayName : null, r.CreatedAt, r.DueAt, r.ResolvedAt));
}
