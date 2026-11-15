using CivicOps.Web.Data;
using CivicOps.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Web.Tests;

public class ServiceTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly Actor Admin = new("a", "Admin", Roles.Admin);
    private static readonly Actor Viewer = new("v", "Viewer", Roles.Viewer);

    private async Task<(RequestQueryService q, RequestCommandService c, Actor agent, Actor sup)> Svcs()
    {
        var sp = app.Services.CreateScope().ServiceProvider;
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var agent = await users.FindByEmailAsync("agent@civicops.demo");
        var sup = await users.FindByEmailAsync("supervisor@civicops.demo");
        return (sp.GetRequiredService<RequestQueryService>(), sp.GetRequiredService<RequestCommandService>(),
            new Actor(agent!.Id, agent.DisplayName, Roles.Agent), new Actor(sup!.Id, sup.DisplayName, Roles.Supervisor));
    }

    private async Task<int> NewRequestIdAsync(RequestStatus status, string? assigneeId = null)
    {
        await using var db = await app.Db.CreateDbContextAsync();
        var r = new ServiceRequest
        {
            Number = "T-" + Guid.NewGuid().ToString("N")[..10], Title = "Test", Description = "d", Address = "1 Test St", RequesterName = "T",
            CategoryId = (await db.Categories.FirstAsync()).Id, DistrictId = (await db.Districts.FirstAsync()).Id,
            Status = status, AssigneeId = assigneeId, CreatedAt = DateTime.UtcNow, DueAt = DateTime.UtcNow.AddDays(3), UpdatedAt = DateTime.UtcNow
        };
        db.Requests.Add(r);
        await db.SaveChangesAsync();
        return r.Id;
    }

    [Fact]
    public async Task Seed_creates_about_500_requests_over_twelve_months()
    {
        await using var db = await app.Db.CreateDbContextAsync();
        Assert.Equal(DemoDataSeeder.RequestCount, await db.Requests.CountAsync(r => !r.Number.StartsWith("T-")));
        Assert.True(await db.Requests.MinAsync(r => r.CreatedAt) < DateTime.UtcNow.AddDays(-330));
        Assert.True(await db.Categories.CountAsync() >= 8);
        Assert.Equal(await db.Categories.CountAsync() * 4, await db.SlaRules.CountAsync());
    }

    [Fact]
    public async Task Paging_returns_requested_page_and_total()
    {
        var (q, _, _, _) = await Svcs();
        var p0 = await q.QueryAsync(new RequestQuery { PageSize = 25, Page = 0, Sort = SortField.Number, Descending = false }, Admin);
        var p1 = await q.QueryAsync(new RequestQuery { PageSize = 25, Page = 1, Sort = SortField.Number, Descending = false }, Admin);
        Assert.Equal(25, p0.Items.Count);
        Assert.True(p0.Total >= DemoDataSeeder.RequestCount);
        Assert.Empty(p0.Items.Select(i => i.Id).Intersect(p1.Items.Select(i => i.Id)));
        Assert.True(string.CompareOrdinal(p0.Items[^1].Number, p1.Items[0].Number) < 0);
    }

    [Fact]
    public async Task Out_of_range_page_is_clamped()
    {
        var (q, _, _, _) = await Svcs();
        var r = await q.QueryAsync(new RequestQuery { PageSize = 10, Page = 9999 }, Admin);
        Assert.NotEmpty(r.Items);
    }

    [Fact]
    public async Task Sorting_by_due_date_orders_rows()
    {
        var (q, _, _, _) = await Svcs();
        var asc = (await q.QueryAsync(new RequestQuery { Sort = SortField.Due, Descending = false, PageSize = 50 }, Admin)).Items;
        Assert.Equal(asc.OrderBy(r => r.DueAt).Select(r => r.Id), asc.Select(r => r.Id));
        var desc = (await q.QueryAsync(new RequestQuery { Sort = SortField.Due, Descending = true, PageSize = 50 }, Admin)).Items;
        Assert.True(desc[0].DueAt >= desc[^1].DueAt);
    }

    [Fact]
    public async Task Filters_combine_status_and_search()
    {
        var (q, _, _, _) = await Svcs();
        var closed = await q.QueryAsync(new RequestQuery { Status = RequestStatus.Closed, PageSize = 100 }, Admin);
        Assert.All(closed.Items, r => Assert.Equal(RequestStatus.Closed, r.Status));

        var pothole = await q.QueryAsync(new RequestQuery { Search = "pothole", PageSize = 100 }, Admin);
        Assert.NotEmpty(pothole.Items);
        Assert.All(pothole.Items, r => Assert.Contains("pothole", r.Title, StringComparison.OrdinalIgnoreCase));

        var none = await q.QueryAsync(new RequestQuery { Search = "zzzz-no-match" }, Admin);
        Assert.Equal(0, none.Total);
    }

    [Fact]
    public async Task Quick_filters_overdue_unassigned_and_mine()
    {
        var (q, _, agent, _) = await Svcs();
        var overdue = await q.QueryAsync(new RequestQuery { Quick = QuickFilter.Overdue, PageSize = 100 }, Admin);
        Assert.NotEmpty(overdue.Items);
        Assert.All(overdue.Items, r => Assert.True(r.IsOverdue(DateTime.UtcNow.AddMinutes(1))));

        var unassigned = await q.QueryAsync(new RequestQuery { Quick = QuickFilter.Unassigned, PageSize = 100 }, Admin);
        Assert.All(unassigned.Items, r => Assert.Null(r.Assignee));

        var mine = await q.QueryAsync(new RequestQuery { Quick = QuickFilter.Mine, PageSize = 100 }, agent);
        Assert.NotEmpty(mine.Items);
        Assert.All(mine.Items, r => Assert.Equal("Sam Okafor", r.Assignee));
    }

    [Theory]
    [InlineData(RequestStatus.New, RequestStatus.InProgress, true)]
    [InlineData(RequestStatus.New, RequestStatus.Resolved, false)]
    [InlineData(RequestStatus.InProgress, RequestStatus.Resolved, true)]
    [InlineData(RequestStatus.OnHold, RequestStatus.Resolved, false)]
    [InlineData(RequestStatus.Resolved, RequestStatus.Closed, true)]
    [InlineData(RequestStatus.Resolved, RequestStatus.InProgress, true)]
    [InlineData(RequestStatus.Closed, RequestStatus.InProgress, false)]
    [InlineData(RequestStatus.Canceled, RequestStatus.New, false)]
    public void Status_transition_rules(RequestStatus from, RequestStatus to, bool allowed) =>
        Assert.Equal(allowed, StatusRules.CanTransition(from, to));

    [Fact]
    public void Terminal_statuses_have_no_next_state()
    {
        Assert.Empty(StatusRules.AllowedNext(RequestStatus.Closed));
        Assert.Empty(StatusRules.AllowedNext(RequestStatus.Canceled));
    }

    [Fact]
    public async Task Invalid_transition_is_rejected_and_valid_one_records_an_event()
    {
        var (_, c, _, sup) = await Svcs();
        var id = await NewRequestIdAsync(RequestStatus.New);
        var bad = await c.ChangeStatusAsync(sup, id, RequestStatus.Closed, null);
        Assert.Equal(Failure.Invalid, bad.Failure);

        var ok = await c.ChangeStatusAsync(sup, id, RequestStatus.InProgress, null);
        Assert.True(ok.Ok);
        var d = await c.GetAsync(id);
        Assert.Equal(RequestStatus.InProgress, d!.Status);
        Assert.Contains(d.Events, e => e.Kind == EventKind.StatusChanged && e.ToStatus == RequestStatus.InProgress && e.Actor == sup.Name);
    }

    [Fact]
    public async Task Resolving_needs_a_note_and_sets_resolved_date()
    {
        var (_, c, _, sup) = await Svcs();
        var id = await NewRequestIdAsync(RequestStatus.InProgress);
        Assert.Equal(Failure.Invalid, (await c.ChangeStatusAsync(sup, id, RequestStatus.Resolved, "  ")).Failure);
        Assert.True((await c.ChangeStatusAsync(sup, id, RequestStatus.Resolved, "Fixed on site.")).Ok);
        Assert.NotNull((await c.GetAsync(id))!.ResolvedAt);
        Assert.True((await c.ChangeStatusAsync(sup, id, RequestStatus.InProgress, "Reopened after complaint.")).Ok);
        Assert.Null((await c.GetAsync(id))!.ResolvedAt);
    }

    [Fact]
    public async Task Viewer_cannot_change_anything()
    {
        var (_, c, _, _) = await Svcs();
        var id = await NewRequestIdAsync(RequestStatus.New);
        Assert.Equal(Failure.Forbidden, (await c.ChangeStatusAsync(Viewer, id, RequestStatus.InProgress, null)).Failure);
        Assert.Equal(Failure.Forbidden, (await c.AddNoteAsync(Viewer, id, "hello")).Failure);
        Assert.Equal(Failure.Forbidden, (await c.AssignAsync(Viewer, id, null)).Failure);
        Assert.Equal(Failure.Forbidden, (await c.ChangeStatusAsync(null, id, RequestStatus.InProgress, null)).Failure);
    }

    [Fact]
    public async Task Agent_can_only_update_own_requests_and_cannot_assign()
    {
        var (_, c, agent, sup) = await Svcs();
        var other = await NewRequestIdAsync(RequestStatus.New);
        var mine = await NewRequestIdAsync(RequestStatus.New, agent.UserId);
        Assert.Equal(Failure.Forbidden, (await c.ChangeStatusAsync(agent, other, RequestStatus.InProgress, null)).Failure);
        Assert.True((await c.ChangeStatusAsync(agent, mine, RequestStatus.InProgress, null)).Ok);
        Assert.Equal(Failure.Forbidden, (await c.AssignAsync(agent, other, agent.UserId)).Failure);
        Assert.True((await c.AssignAsync(sup, other, agent.UserId)).Ok);
        Assert.Equal(agent.UserId, (await c.GetAsync(other))!.AssigneeId);
    }

    [Fact]
    public async Task Notes_are_validated()
    {
        var (_, c, agent, _) = await Svcs();
        var id = await NewRequestIdAsync(RequestStatus.New, agent.UserId);
        Assert.Equal(Failure.Invalid, (await c.AddNoteAsync(agent, id, " ")).Failure);
        Assert.Equal(Failure.Invalid, (await c.AddNoteAsync(agent, id, new string('x', 1001))).Failure);
        Assert.True((await c.AddNoteAsync(agent, id, "Called the resident.")).Ok);
        Assert.Equal(Failure.NotFound, (await c.AddNoteAsync(agent, -5, "x")).Failure);
    }

    [Fact]
    public async Task Only_admin_can_edit_sla_rules_or_reset()
    {
        var (_, _, _, sup) = await Svcs();
        var admin = app.Services.CreateScope().ServiceProvider.GetRequiredService<AdminService>();
        var cat = (await admin.GetCategoriesAsync()).First();
        var hours = cat.SlaHours.ToDictionary(k => k.Key, k => k.Value + 1);
        Assert.Equal(Failure.Forbidden, (await admin.SaveCategoryAsync(sup, cat.Id, cat.Name, cat.Department, true, hours)).Failure);
        Assert.True((await admin.SaveCategoryAsync(Admin, cat.Id, cat.Name, cat.Department, true, hours)).Ok);
        var after = (await admin.GetCategoriesAsync()).First(x => x.Id == cat.Id);
        Assert.Equal(hours[RequestPriority.Urgent], after.SlaHours[RequestPriority.Urgent]);
        hours[RequestPriority.Urgent] = 0;
        Assert.Equal(Failure.Invalid, (await admin.SaveCategoryAsync(Admin, cat.Id, cat.Name, cat.Department, true, hours)).Failure);
        Assert.Equal(Failure.Forbidden, (await admin.ResetDemoDataAsync(sup)).Failure);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("plain", "plain")]
    public void Csv_escaping(string input, string expected) => Assert.Equal(expected, CsvExport.Escape(input));

    [Fact]
    public async Task Dashboard_kpis_are_consistent()
    {
        var d = await app.Services.CreateScope().ServiceProvider.GetRequiredService<DashboardService>().GetAsync();
        Assert.InRange(d.Kpis.SlaCompliancePct, 0, 100);
        Assert.True(d.Kpis.Overdue <= d.Kpis.Open);
        Assert.Equal(12, d.CreatedPerWeek.Count);
        Assert.Equal(12, d.SlaTrend.Count);
        Assert.Equal(d.Kpis.Total, d.Districts.Sum(x => x.Total));
    }
}
