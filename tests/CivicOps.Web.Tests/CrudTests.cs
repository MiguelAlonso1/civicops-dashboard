using CivicOps.Web.Data;
using CivicOps.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Web.Tests;

public class CrudTests(TestApp app) : IClassFixture<TestApp>
{
    private IServiceProvider Sp => app.Services.CreateScope().ServiceProvider;

    private async Task<Actor> ActorAsync(string role)
    {
        var u = await Sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(DemoDataSeeder.DemoLogins[role].Email);
        return new Actor(u!.Id, u.DisplayName, role);
    }

    private async Task<RequestInput> InputAsync(string title = "Broken bench", RequestPriority p = RequestPriority.Normal)
    {
        await using var db = await app.Db.CreateDbContextAsync();
        var cat = await db.Categories.OrderBy(c => c.Id).FirstAsync(c => c.IsActive);
        var dist = await db.Districts.OrderBy(d => d.Id).FirstAsync();
        return new RequestInput(title, "Bench slats are broken.", cat.Id, dist.Id, p, "12 Test Ave", "Pat Resident");
    }

    private async Task<int> CreateAsync(Actor a, string title = "Broken bench")
    {
        var r = await Sp.GetRequiredService<RequestCommandService>().CreateAsync(a, await InputAsync(title));
        Assert.True(r.Ok, r.Result.Message);
        return r.Id!.Value;
    }

    // ---- requests: create ----

    [Theory]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.Supervisor, true)]
    [InlineData(Roles.Agent, true)]
    [InlineData(Roles.Viewer, false)]
    public async Task Create_permissions_by_role(string role, bool allowed)
    {
        var r = await Sp.GetRequiredService<RequestCommandService>().CreateAsync(await ActorAsync(role), await InputAsync());
        Assert.Equal(allowed, r.Ok);
        if (!allowed) Assert.Equal(Failure.Forbidden, r.Result.Failure);
    }

    [Fact]
    public async Task Create_computes_due_date_from_sla_and_logs_created_event()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var input = await InputAsync(p: RequestPriority.High);
        var sup = await ActorAsync(Roles.Supervisor);
        var id = (await svc.CreateAsync(sup, input)).Id!.Value;
        var d = (await svc.GetAsync(id))!;
        var hours = (await svc.GetLookupsAsync()).Categories.First(c => c.Id == input.CategoryId).SlaHours[RequestPriority.High];
        Assert.Equal(d.CreatedAt.AddHours(hours), d.DueAt);
        Assert.Equal(RequestStatus.New, d.Status);
        Assert.StartsWith("SR-", d.Number);
        Assert.Contains(d.Events, e => e.Kind == EventKind.Created && e.Actor == sup.Name && e.ActorId == sup.UserId);
        Assert.Null(d.AssigneeId);
    }

    [Fact]
    public async Task Create_numbers_are_unique_and_agent_gets_assigned()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var agent = await ActorAsync(Roles.Agent);
        var a = await CreateAsync(agent);
        var b = await CreateAsync(agent);
        var da = (await svc.GetAsync(a))!;
        Assert.NotEqual(da.Number, (await svc.GetAsync(b))!.Number);
        Assert.Equal(agent.UserId, da.AssigneeId);
    }

    [Fact]
    public async Task Create_validates_fields()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var sup = await ActorAsync(Roles.Supervisor);
        var ok = await InputAsync();
        foreach (var bad in new[]
        {
            ok with { Title = " " }, ok with { Title = new string('x', 161) }, ok with { Description = "" }, ok with { Address = null },
            ok with { RequesterName = "" }, ok with { CategoryId = -1 }, ok with { DistrictId = -1 }, ok with { Priority = (RequestPriority)9 }
        })
            Assert.Equal(Failure.Invalid, (await svc.CreateAsync(sup, bad)).Result.Failure);
    }

    [Fact]
    public async Task Create_rejects_inactive_category()
    {
        var admin = Sp.GetRequiredService<AdminService>();
        var adm = await ActorAsync(Roles.Admin);
        Assert.True((await admin.SaveCategoryAsync(adm, null, "Dormant", "Test", false, Enum.GetValues<RequestPriority>().ToDictionary(p => p, _ => 10))).Ok);
        var cat = (await admin.GetCategoriesAsync()).First(c => c.Name == "Dormant");
        var r = await Sp.GetRequiredService<RequestCommandService>().CreateAsync(adm, (await InputAsync()) with { CategoryId = cat.Id });
        Assert.Equal(Failure.Invalid, r.Result.Failure);
    }

    // ---- requests: edit ----

    [Fact]
    public async Task Supervisor_and_admin_can_edit_any_request_and_edit_is_logged()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var id = await CreateAsync(await ActorAsync(Roles.Admin));
        foreach (var role in new[] { Roles.Supervisor, Roles.Admin })
        {
            var a = await ActorAsync(role);
            var res = await svc.EditAsync(a, id, (await InputAsync($"Edited by {role}")) with { Priority = RequestPriority.Urgent });
            Assert.True(res.Ok, res.Message);
            var d = (await svc.GetAsync(id))!;
            Assert.Equal($"Edited by {role}", d.Title);
            Assert.Equal(RequestPriority.Urgent, d.Priority);
            Assert.Contains(d.Events, e => e.Kind == EventKind.Edited && e.Actor == a.Name);
        }
    }

    [Fact]
    public async Task Edit_recomputes_due_date_when_priority_changes()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var sup = await ActorAsync(Roles.Supervisor);
        var id = await CreateAsync(sup);
        var before = (await svc.GetAsync(id))!;
        Assert.True((await svc.EditAsync(sup, id, (await InputAsync()) with { Priority = RequestPriority.Urgent })).Ok);
        var after = (await svc.GetAsync(id))!;
        Assert.True(after.DueAt < before.DueAt);
    }

    [Fact]
    public async Task Edit_without_changes_adds_no_event()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var sup = await ActorAsync(Roles.Supervisor);
        var id = await CreateAsync(sup);
        var n = (await svc.GetAsync(id))!.Events.Count;
        Assert.True((await svc.EditAsync(sup, id, await InputAsync())).Ok);
        Assert.Equal(n, (await svc.GetAsync(id))!.Events.Count);
    }

    [Fact]
    public async Task Agent_edits_only_own_open_requests()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var agent = await ActorAsync(Roles.Agent);
        var sup = await ActorAsync(Roles.Supervisor);
        var others = await CreateAsync(sup);
        var mine = await CreateAsync(agent);
        var input = await InputAsync("Agent edit");
        Assert.Equal(Failure.Forbidden, (await svc.EditAsync(agent, others, input)).Failure);
        Assert.True((await svc.EditAsync(agent, mine, input)).Ok);

        // Closed requests are locked for agents but not for supervisors.
        await using (var db = await app.Db.CreateDbContextAsync())
            await db.Requests.Where(r => r.Id == mine).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Closed));
        Assert.Equal(Failure.Forbidden, (await svc.EditAsync(agent, mine, input with { Title = "Again" })).Failure);
        Assert.True((await svc.EditAsync(sup, mine, input with { Title = "Supervisor fix" })).Ok);
    }

    [Fact]
    public async Task Viewer_and_anonymous_cannot_edit_or_delete()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var id = await CreateAsync(await ActorAsync(Roles.Admin));
        var viewer = await ActorAsync(Roles.Viewer);
        Assert.Equal(Failure.Forbidden, (await svc.EditAsync(viewer, id, await InputAsync())).Failure);
        Assert.Equal(Failure.Forbidden, (await svc.EditAsync(null, id, await InputAsync())).Failure);
        Assert.Equal(Failure.Forbidden, (await svc.DeleteAsync(viewer, id)).Failure);
        Assert.Equal(Failure.Forbidden, (await svc.DeleteAsync(null, id)).Failure);
        Assert.Equal(Failure.NotFound, (await svc.EditAsync(await ActorAsync(Roles.Admin), -1, await InputAsync())).Failure);
    }

    // ---- requests: delete ----

    [Theory]
    [InlineData(Roles.Supervisor)]
    [InlineData(Roles.Agent)]
    [InlineData(Roles.Viewer)]
    public async Task Only_admin_can_delete_requests(string role)
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var id = await CreateAsync(await ActorAsync(Roles.Admin));
        Assert.Equal(Failure.Forbidden, (await svc.DeleteAsync(await ActorAsync(role), id)).Failure);
        Assert.NotNull(await svc.GetAsync(id));
    }

    [Fact]
    public async Task Admin_delete_removes_request_and_events_and_writes_audit()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var admin = await ActorAsync(Roles.Admin);
        var id = await CreateAsync(admin, "Delete me");
        Assert.True((await svc.AddNoteAsync(admin, id, "a note")).Ok);
        var number = (await svc.GetAsync(id))!.Number;

        Assert.True((await svc.DeleteAsync(admin, id)).Ok);
        Assert.Null(await svc.GetAsync(id));
        await using var db = await app.Db.CreateDbContextAsync();
        Assert.False(await db.Events.AnyAsync(e => e.ServiceRequestId == id));
        var audit = await db.AuditLogs.SingleAsync(a => a.EntityKey == number);
        Assert.Equal("Request deleted", audit.Action);
        Assert.Equal(admin.UserId, audit.ActorId);
        Assert.Contains("Delete me", audit.Details);
        Assert.Equal(Failure.NotFound, (await svc.DeleteAsync(admin, id)).Failure);
    }

    // ---- notes ----

    [Fact]
    public async Task Authors_edit_and_delete_own_notes_admin_any_others_denied()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var admin = await ActorAsync(Roles.Admin);
        var sup = await ActorAsync(Roles.Supervisor);
        var agent = await ActorAsync(Roles.Agent);
        var viewer = await ActorAsync(Roles.Viewer);
        var id = await CreateAsync(admin);
        Assert.True((await svc.AddNoteAsync(sup, id, "supervisor note")).Ok);
        int NoteId(RequestView d) => d.Events.First(e => e.Kind == EventKind.Note).Id;
        var noteId = NoteId((await svc.GetAsync(id))!);

        Assert.Equal(Failure.Forbidden, (await svc.EditNoteAsync(agent, noteId, "hijack")).Failure);
        Assert.Equal(Failure.Forbidden, (await svc.DeleteNoteAsync(agent, noteId)).Failure);
        Assert.Equal(Failure.Forbidden, (await svc.EditNoteAsync(viewer, noteId, "x")).Failure);
        Assert.Equal(Failure.Invalid, (await svc.EditNoteAsync(sup, noteId, " ")).Failure);

        Assert.True((await svc.EditNoteAsync(sup, noteId, "revised")).Ok);
        var edited = (await svc.GetAsync(id))!.Events.First(e => e.Id == noteId);
        Assert.Equal("revised", edited.Note);
        Assert.NotNull(edited.EditedAt);

        Assert.True((await svc.EditNoteAsync(admin, noteId, "admin edit")).Ok);
        Assert.True((await svc.DeleteNoteAsync(sup, noteId)).Ok);
        Assert.DoesNotContain((await svc.GetAsync(id))!.Events, e => e.Id == noteId);
    }

    [Fact]
    public async Task System_events_cannot_be_edited_or_deleted_even_by_admin()
    {
        var svc = Sp.GetRequiredService<RequestCommandService>();
        var admin = await ActorAsync(Roles.Admin);
        var id = await CreateAsync(admin);
        var created = (await svc.GetAsync(id))!.Events.First(e => e.Kind == EventKind.Created);
        Assert.Equal(Failure.NotFound, (await svc.EditNoteAsync(admin, created.Id, "x")).Failure);
        Assert.Equal(Failure.NotFound, (await svc.DeleteNoteAsync(admin, created.Id)).Failure);
    }

    // ---- categories ----

    [Fact]
    public async Task Category_delete_is_blocked_when_referenced_and_admin_only()
    {
        var admin = Sp.GetRequiredService<AdminService>();
        var adm = await ActorAsync(Roles.Admin);
        var used = (await admin.GetCategoriesAsync()).First(c => c.RequestCount > 0);
        var blocked = await admin.DeleteCategoryAsync(adm, used.Id);
        Assert.Equal(Failure.Invalid, blocked.Failure);
        Assert.Contains("Deactivate", blocked.Message);

        Assert.True((await admin.SaveCategoryAsync(adm, null, "Temp cat", "Test", true, Enum.GetValues<RequestPriority>().ToDictionary(p => p, _ => 5))).Ok);
        var temp = (await admin.GetCategoriesAsync()).First(c => c.Name == "Temp cat");
        Assert.Equal(Failure.Forbidden, (await admin.DeleteCategoryAsync(await ActorAsync(Roles.Supervisor), temp.Id)).Failure);
        Assert.True((await admin.DeleteCategoryAsync(adm, temp.Id)).Ok);
        Assert.DoesNotContain(await admin.GetCategoriesAsync(), c => c.Name == "Temp cat");
        await using var db = await app.Db.CreateDbContextAsync();
        Assert.False(await db.SlaRules.AnyAsync(s => s.CategoryId == temp.Id));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Category deleted" && a.EntityKey == "Temp cat"));
    }

    // ---- districts ----

    [Fact]
    public async Task District_crud_validation_and_delete_blocking()
    {
        var admin = Sp.GetRequiredService<AdminService>();
        var adm = await ActorAsync(Roles.Admin);
        var sup = await ActorAsync(Roles.Supervisor);
        Assert.Equal(Failure.Forbidden, (await admin.SaveDistrictAsync(sup, null, "X1", "Nope", 1)).Failure);
        Assert.Equal(Failure.Invalid, (await admin.SaveDistrictAsync(adm, null, "", "Nope", 1)).Failure);
        Assert.Equal(Failure.Invalid, (await admin.SaveDistrictAsync(adm, null, "X1", "Nope", -1)).Failure);
        Assert.Equal(Failure.Invalid, (await admin.SaveDistrictAsync(adm, null, "d1", "Duplicate code", 1)).Failure);

        Assert.True((await admin.SaveDistrictAsync(adm, null, "x9", "Test Heights", 1200)).Ok);
        var d = (await admin.GetDistrictsAsync()).First(x => x.Code == "X9");
        Assert.True((await admin.SaveDistrictAsync(adm, d.Id, "X9", "Test Heights East", 1500)).Ok);
        Assert.Equal(1500, (await admin.GetDistrictsAsync()).First(x => x.Id == d.Id).Population);

        // Referenced by a request: delete blocked.
        var input = (await InputAsync()) with { DistrictId = d.Id };
        var rid = (await Sp.GetRequiredService<RequestCommandService>().CreateAsync(adm, input)).Id!.Value;
        Assert.Equal(Failure.Invalid, (await admin.DeleteDistrictAsync(adm, d.Id)).Failure);
        Assert.True((await Sp.GetRequiredService<RequestCommandService>().DeleteAsync(adm, rid)).Ok);

        Assert.Equal(Failure.Forbidden, (await admin.DeleteDistrictAsync(sup, d.Id)).Failure);
        Assert.True((await admin.DeleteDistrictAsync(adm, d.Id)).Ok);
        Assert.DoesNotContain(await admin.GetDistrictsAsync(), x => x.Id == d.Id);
    }

    // ---- users ----

    [Fact]
    public async Task User_create_validates_and_non_admins_are_denied()
    {
        var svc = Sp.GetRequiredService<UserAdminService>();
        var adm = await ActorAsync(Roles.Admin);
        foreach (var role in new[] { Roles.Supervisor, Roles.Agent, Roles.Viewer })
            Assert.Equal(Failure.Forbidden, (await svc.CreateAsync(await ActorAsync(role), "n@civicops.demo", "N", Roles.Agent, "Str0ng!Passw0rd")).Failure);
        Assert.Empty(await svc.ListAsync(await ActorAsync(Roles.Viewer)));

        Assert.Equal(Failure.Invalid, (await svc.CreateAsync(adm, "not-an-email", "N", Roles.Agent, "Str0ng!Passw0rd")).Failure);
        Assert.Equal(Failure.Invalid, (await svc.CreateAsync(adm, "n1@civicops.demo", "", Roles.Agent, "Str0ng!Passw0rd")).Failure);
        Assert.Equal(Failure.Invalid, (await svc.CreateAsync(adm, "n1@civicops.demo", "N", "Boss", "Str0ng!Passw0rd")).Failure);
        Assert.Equal(Failure.Invalid, (await svc.CreateAsync(adm, "n1@civicops.demo", "N", Roles.Agent, "short")).Failure);
        Assert.Equal(Failure.Invalid, (await svc.CreateAsync(adm, "agent@civicops.demo", "Dup", Roles.Agent, "Str0ng!Passw0rd")).Failure);
        Assert.True((await svc.CreateAsync(adm, "created@civicops.demo", "Created User", Roles.Supervisor, "Str0ng!Passw0rd")).Ok);
        var row = (await svc.ListAsync(adm)).Single(u => u.Email == "created@civicops.demo");
        Assert.Equal(Roles.Supervisor, row.Role);
        Assert.False(row.IsLocked);
    }

    [Fact]
    public async Task User_role_change_lock_unlock_and_delete()
    {
        var svc = Sp.GetRequiredService<UserAdminService>();
        var adm = await ActorAsync(Roles.Admin);
        Assert.True((await svc.CreateAsync(adm, "temp1@civicops.demo", "Temp One", Roles.Viewer, "Str0ng!Passw0rd")).Ok);
        var id = (await svc.ListAsync(adm)).Single(u => u.Email == "temp1@civicops.demo").Id;

        Assert.Equal(Failure.Forbidden, (await svc.ChangeRoleAsync(await ActorAsync(Roles.Supervisor), id, Roles.Admin)).Failure);
        Assert.Equal(Failure.Invalid, (await svc.ChangeRoleAsync(adm, id, "Boss")).Failure);
        Assert.True((await svc.ChangeRoleAsync(adm, id, Roles.Agent)).Ok);
        Assert.Equal(Roles.Agent, (await svc.ListAsync(adm)).Single(u => u.Id == id).Role);

        Assert.True((await svc.SetLockedAsync(adm, id, true)).Ok);
        Assert.True((await svc.ListAsync(adm)).Single(u => u.Id == id).IsLocked);
        var users = Sp.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync(id))!));
        Assert.True((await svc.SetLockedAsync(adm, id, false)).Ok);
        Assert.False((await svc.ListAsync(adm)).Single(u => u.Id == id).IsLocked);

        Assert.Equal(Failure.Forbidden, (await svc.DeleteAsync(await ActorAsync(Roles.Agent), id)).Failure);
        Assert.True((await svc.DeleteAsync(adm, id)).Ok);
        Assert.Null(await Sp.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(id));
        await using var db = await app.Db.CreateDbContextAsync();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "User deleted" && a.EntityKey == "temp1@civicops.demo"));
    }

    [Fact]
    public async Task Deleting_a_user_unassigns_their_requests()
    {
        var svc = Sp.GetRequiredService<UserAdminService>();
        var adm = await ActorAsync(Roles.Admin);
        Assert.True((await svc.CreateAsync(adm, "temp2@civicops.demo", "Temp Two", Roles.Agent, "Str0ng!Passw0rd")).Ok);
        var id = (await svc.ListAsync(adm)).Single(u => u.Email == "temp2@civicops.demo").Id;
        var rid = await CreateAsync(new Actor(id, "Temp Two", Roles.Agent));
        Assert.True((await svc.DeleteAsync(adm, id)).Ok);
        Assert.Null((await Sp.GetRequiredService<RequestCommandService>().GetAsync(rid))!.AssigneeId);
    }

    [Fact]
    public async Task Admin_cannot_delete_lock_or_demote_themselves()
    {
        var svc = Sp.GetRequiredService<UserAdminService>();
        var adm = await ActorAsync(Roles.Admin);
        Assert.Equal(Failure.Invalid, (await svc.DeleteAsync(adm, adm.UserId)).Failure);
        Assert.Equal(Failure.Invalid, (await svc.SetLockedAsync(adm, adm.UserId, true)).Failure);
        Assert.Equal(Failure.Invalid, (await svc.ChangeRoleAsync(adm, adm.UserId, Roles.Viewer)).Failure);
    }

    [Fact]
    public async Task Last_admin_cannot_be_deleted_demoted_or_locked()
    {
        var svc = Sp.GetRequiredService<UserAdminService>();
        var users = Sp.GetRequiredService<UserManager<ApplicationUser>>();
        var adm = await ActorAsync(Roles.Admin);
        // A second admin acts; the seeded admin is the only other Admin, so it is protected once the acting one is the sole admin.
        Assert.True((await svc.CreateAsync(adm, "admin2@civicops.demo", "Second Admin", Roles.Admin, "Str0ng!Passw0rd")).Ok);
        var second = (await svc.ListAsync(adm)).Single(u => u.Email == "admin2@civicops.demo");
        var secondActor = new Actor(second.Id, second.DisplayName, Roles.Admin);

        // Two admins: the original can be demoted by the second one.
        Assert.True((await svc.ChangeRoleAsync(secondActor, adm.UserId, Roles.Supervisor)).Ok);
        // Now the second admin is the last one. A (non-self) actor claiming Admin is impossible, so test via a stale actor:
        var stale = new Actor(adm.UserId, adm.Name, Roles.Admin);
        Assert.Equal(Failure.Invalid, (await svc.DeleteAsync(stale, second.Id)).Failure);
        Assert.Contains("last Admin", (await svc.DeleteAsync(stale, second.Id)).Message);
        Assert.Equal(Failure.Invalid, (await svc.ChangeRoleAsync(stale, second.Id, Roles.Agent)).Failure);
        Assert.Equal(Failure.Invalid, (await svc.SetLockedAsync(stale, second.Id, true)).Failure);
        Assert.NotNull(await users.FindByIdAsync(second.Id));

        // Restore for other tests.
        Assert.True((await svc.ChangeRoleAsync(secondActor, adm.UserId, Roles.Admin)).Ok);
    }

    [Fact]
    public async Task Locked_user_cannot_use_one_click_login()
    {
        var svc = Sp.GetRequiredService<UserAdminService>();
        var adm = await ActorAsync(Roles.Admin);
        var viewer = await ActorAsync(Roles.Viewer);
        Assert.True((await svc.SetLockedAsync(adm, viewer.UserId, true)).Ok);
        try
        {
            var c = app.NewClient();
            var page = await c.GetStringAsync("/login");
            var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            var res = await c.PostAsync("/account/demo", new FormUrlEncodedContent(new Dictionary<string, string> { ["role"] = Roles.Viewer, ["__RequestVerificationToken"] = token }));
            Assert.Contains("error=2", res.Headers.Location!.OriginalString);
        }
        finally { await svc.SetLockedAsync(adm, viewer.UserId, false); }
    }

    [Fact]
    public async Task Schema_version_matches_and_is_stored()
    {
        await using var db = await app.Db.CreateDbContextAsync();
        var v = await db.Database.SqlQueryRaw<int>("SELECT user_version AS Value FROM pragma_user_version").SingleAsync();
        Assert.Equal(SchemaVersion.Current, v);
    }

    [Fact]
    public async Task Outdated_schema_is_recreated_and_reseeded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"civicops-schema-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<CivicOpsDb>().UseSqlite($"Data Source={path}").Options;
        IDbContextFactory<CivicOpsDb> factory = new TestFactory(opts);
        try
        {
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.Districts.Add(new District { Code = "OLD", Name = "Old data" });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 1");
            }
            await SchemaVersion.EnsureAsync(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.False(await db.Districts.AnyAsync());
                Assert.Equal(SchemaVersion.Current, await db.Database.SqlQueryRaw<int>("SELECT user_version AS Value FROM pragma_user_version").SingleAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private sealed class TestFactory(DbContextOptions<CivicOpsDb> o) : IDbContextFactory<CivicOpsDb>
    {
        public CivicOpsDb CreateDbContext() => new(o);
    }
}
