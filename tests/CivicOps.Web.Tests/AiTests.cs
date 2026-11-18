using System.Net;
using System.Text;
using System.Text.Json;
using CivicOps.Web.Data;
using CivicOps.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CivicOps.Web.Tests;

/// <summary>AI Assist tests: a fake HttpMessageHandler stands in for the Claude API (no live calls).</summary>
public class AiTests(TestApp app) : IClassFixture<TestApp>
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;
        public HttpRequestMessage? LastRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { stop_reason = "end_turn", content = new[] { new { type = "text", text } } }), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Sse(params string[] dataLines) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(dataLines.Select(d => $"event: x\ndata: {d}\n\n")), Encoding.UTF8, "text/event-stream")
    };

    private AiService Make(FakeHandler h, string? key = "test-key", string? daily = null, string? model = null)
    {
        var cfg = new Dictionary<string, string?> { ["Ai:Provider"] = "Anthropic", ["Claude:ApiKey"] = key, ["Claude:DailyLimit"] = daily, ["Claude:Model"] = model };
        return new AiService(app.Db, new Factory(h), new ConfigurationBuilder().AddInMemoryCollection(cfg).Build(), TimeProvider.System, NullLogger<AiService>.Instance);
    }

    private static Actor NewActor(string role = Roles.Agent) => new("ai-test-" + Guid.NewGuid().ToString("N"), "Test " + role, role);

    private async Task<(string Category, string District)> LookupsAsync()
    {
        await using var db = await app.Db.CreateDbContextAsync();
        return ((await db.Categories.OrderBy(c => c.Id).FirstAsync(c => c.IsActive)).Name, (await db.Districts.OrderBy(d => d.Id).FirstAsync()).Code);
    }

    private static string TriageJson(string category, string priority = "High", string district = "NONE") =>
        JsonSerializer.Serialize(new { category, priority, district, summary = "Water main leak on a street.", rationale = "Flooding risk." });

    private async Task<RequestView> SampleRequestAsync()
    {
        await using var db = await app.Db.CreateDbContextAsync();
        var id = await db.Requests.OrderBy(r => r.Id).Select(r => r.Id).FirstAsync();
        return (await app.Services.CreateScope().ServiceProvider.GetRequiredService<RequestCommandService>().GetAsync(id))!;
    }

    private async Task<int> AuditCountAsync(string userId)
    {
        await using var db = await app.Db.CreateDbContextAsync();
        return await db.AuditLogs.CountAsync(a => a.ActorId == userId);
    }

    // ---- triage ----

    [Fact]
    public async Task Triage_valid_suggestion_is_parsed_and_request_is_well_formed()
    {
        var (cat, dist) = await LookupsAsync();
        var h = new FakeHandler(_ => Json(TriageJson(cat, "Urgent", dist)));
        var actor = NewActor();
        var r = await Make(h).SuggestTriageAsync(actor, "Leak", "Water everywhere");
        Assert.True(r.Ok, r.Result.Message);
        Assert.Equal(cat, r.Value!.Category);
        Assert.Equal(RequestPriority.Urgent, r.Value.Priority);
        Assert.NotNull(r.Value.DistrictId);

        using var body = JsonDocument.Parse(h.LastBody!);
        var root = body.RootElement;
        Assert.Equal("claude-haiku-5-5", root.GetProperty("model").GetString());
        Assert.Equal("ephemeral", root.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Contains(cat, h.LastBody!);
        Assert.Equal("test-key", h.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", h.LastRequest.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task Triage_district_none_maps_to_null_and_model_is_configurable()
    {
        var (cat, _) = await LookupsAsync();
        var h = new FakeHandler(_ => Json(TriageJson(cat)));
        var r = await Make(h, model: "claude-test-model").SuggestTriageAsync(NewActor(), "Leak", "Water");
        Assert.True(r.Ok);
        Assert.Null(r.Value!.DistrictId);
        Assert.Contains("claude-test-model", h.LastBody!);
    }

    [Theory]
    [InlineData("Not a real category", "High", "NONE")]
    [InlineData("CAT", "Catastrophic", "NONE")]
    [InlineData("CAT", "High", "ZZZ")]
    public async Task Triage_unknown_values_are_rejected(string category, string priority, string district)
    {
        var (cat, _) = await LookupsAsync();
        if (category == "CAT") category = cat;
        var h = new FakeHandler(_ => Json(TriageJson(category, priority, district)));
        var r = await Make(h).SuggestTriageAsync(NewActor(), "Leak", "Water");
        Assert.False(r.Ok);
        Assert.Equal(Failure.Invalid, r.Result.Failure);
        Assert.Null(r.Value);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"category\":\"x\"}")]
    public async Task Triage_malformed_output_is_rejected(string text)
    {
        var r = await Make(new FakeHandler(_ => Json(text))).SuggestTriageAsync(NewActor(), "Leak", "Water");
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Triage_enforces_input_lengths_without_calling_the_api()
    {
        var h = new FakeHandler(_ => Json("{}"));
        var ai = Make(h);
        Assert.Equal(Failure.Invalid, (await ai.SuggestTriageAsync(NewActor(), new string('a', AiService.MaxTitleLength + 1), "x")).Result.Failure);
        Assert.Equal(Failure.Invalid, (await ai.SuggestTriageAsync(NewActor(), "t", new string('a', AiService.MaxDescriptionLength + 1))).Result.Failure);
        Assert.Equal(Failure.Invalid, (await ai.SuggestTriageAsync(NewActor(), " ", "x")).Result.Failure);
        Assert.Equal(0, h.Calls);
    }

    // ---- permissions, key, limits ----

    [Fact]
    public async Task Viewer_is_denied_everywhere_and_nothing_is_called_or_logged()
    {
        var h = new FakeHandler(_ => Json("{}"));
        var ai = Make(h);
        var viewer = NewActor(Roles.Viewer);
        var req = await SampleRequestAsync();
        Assert.Equal(Failure.Forbidden, (await ai.SuggestTriageAsync(viewer, "Leak", "Water")).Result.Failure);
        Assert.Equal(Failure.Forbidden, (await ai.BeginDraftAsync(viewer, req)).Result.Failure);
        Assert.Equal(Failure.Forbidden, (await ai.SummarizeTimelineAsync(viewer, req)).Result.Failure);
        Assert.Equal(Failure.Forbidden, (await ai.SuggestTriageAsync(null, "Leak", "Water")).Result.Failure);
        Assert.Equal(0, h.Calls);
        Assert.Equal(0, await AuditCountAsync(viewer.UserId));
    }

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Supervisor)]
    [InlineData(Roles.Agent)]
    public async Task Editing_roles_are_allowed(string role)
    {
        var (cat, _) = await LookupsAsync();
        var r = await Make(new FakeHandler(_ => Json(TriageJson(cat)))).SuggestTriageAsync(NewActor(role), "Leak", "Water");
        Assert.True(r.Ok, r.Result.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Without_api_key_the_feature_is_disabled_and_never_calls_out(string? key)
    {
        var h = new FakeHandler(_ => Json("{}"));
        var ai = Make(h, key);
        Assert.False(ai.IsConfigured);
        var r = await ai.SuggestTriageAsync(NewActor(), "Leak", "Water");
        Assert.Equal(Failure.Unavailable, r.Result.Failure);
        Assert.Contains("API key", r.Result.Message);
        Assert.Equal(Failure.Unavailable, (await ai.BeginDraftAsync(NewActor(), await SampleRequestAsync())).Result.Failure);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Per_user_hourly_limit_is_enforced()
    {
        var (cat, _) = await LookupsAsync();
        var ai = Make(new FakeHandler(_ => Json(TriageJson(cat))), daily: "100000");
        var actor = NewActor();
        for (var i = 0; i < AiService.PerUserHourlyLimit; i++)
            Assert.True((await ai.SuggestTriageAsync(actor, "Leak", "Water")).Ok, $"call {i}");
        var r = await ai.SuggestTriageAsync(actor, "Leak", "Water");
        Assert.Equal(Failure.Limited, r.Result.Failure);
        // Another user is not affected.
        Assert.True((await ai.SuggestTriageAsync(NewActor(), "Leak", "Water")).Ok);
    }

    [Fact]
    public async Task Global_daily_cap_is_enforced()
    {
        var (cat, _) = await LookupsAsync();
        await using (var db = await app.Db.CreateDbContextAsync())
        {
            var used = await db.AuditLogs.CountAsync(a => a.Action.StartsWith("AI ") && a.At >= DateTime.UtcNow.Date);
            var ai = Make(new FakeHandler(_ => Json(TriageJson(cat))), daily: (used + 2).ToString());
            Assert.True((await ai.SuggestTriageAsync(NewActor(), "Leak", "Water")).Ok);
            Assert.True((await ai.SuggestTriageAsync(NewActor(), "Leak", "Water")).Ok);
            var r = await ai.SuggestTriageAsync(NewActor(), "Leak", "Water");
            Assert.Equal(Failure.Limited, r.Result.Failure);
            Assert.Contains("daily", r.Result.Message);
        }
    }

    [Fact]
    public async Task Usage_is_audited_without_prompt_text()
    {
        var (cat, _) = await LookupsAsync();
        var actor = NewActor();
        await Make(new FakeHandler(_ => Json(TriageJson(cat)))).SuggestTriageAsync(actor, "Secret title zebra", "Secret description zebra");
        await using var db = await app.Db.CreateDbContextAsync();
        var row = await db.AuditLogs.SingleAsync(a => a.ActorId == actor.UserId);
        Assert.Equal("AI triage", row.Action);
        Assert.DoesNotContain("zebra", row.Details ?? "");
        Assert.DoesNotContain("zebra", row.EntityKey);
    }

    // ---- API errors ----

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "busy")]
    [InlineData(HttpStatusCode.InternalServerError, "could not complete")]
    public async Task Api_errors_become_friendly_messages(HttpStatusCode code, string expected)
    {
        var h = new FakeHandler(_ => new HttpResponseMessage(code) { Content = new StringContent("{\"error\":{\"message\":\"sk-ant-secret\"}}") });
        var r = await Make(h).SuggestTriageAsync(NewActor(), "Leak", "Water");
        Assert.Equal(Failure.Unavailable, r.Result.Failure);
        Assert.Contains(expected, r.Result.Message);
        Assert.DoesNotContain("sk-ant", r.Result.Message);
    }

    [Fact]
    public async Task Network_failure_is_friendly()
    {
        var h = new FakeHandler(_ => throw new HttpRequestException("boom"));
        var r = await Make(h).SuggestTriageAsync(NewActor(), "Leak", "Water");
        Assert.Equal(Failure.Unavailable, r.Result.Failure);
        Assert.DoesNotContain("boom", r.Result.Message);
    }

    // ---- streaming draft and summary ----

    [Fact]
    public async Task Draft_streams_text_deltas_and_ignores_other_events()
    {
        var h = new FakeHandler(_ => Sse(
            "{\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}",
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\"}}",
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hidden\"}}",
            "{\"type\":\"ping\"}",
            "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello \"}}",
            "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Resident.\"}}",
            "{\"type\":\"message_stop\"}"));
        var actor = NewActor();
        var s = await Make(h).BeginDraftAsync(actor, await SampleRequestAsync());
        Assert.True(s.Ok, s.Result.Message);
        var sb = new StringBuilder();
        await foreach (var c in s.Chunks!) sb.Append(c);
        Assert.Equal("Hello Resident.", sb.ToString());
        Assert.Contains("\"stream\":true", h.LastBody);
        Assert.Contains("SLA due date", h.LastBody);
        await using var db = await app.Db.CreateDbContextAsync();
        Assert.Equal("AI draft", (await db.AuditLogs.SingleAsync(a => a.ActorId == actor.UserId)).Action);
    }

    [Fact]
    public async Task Draft_stream_error_event_and_empty_stream_throw_friendly_errors()
    {
        var err = Make(new FakeHandler(_ => Sse("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}")));
        var s = await err.BeginDraftAsync(NewActor(), await SampleRequestAsync());
        await Assert.ThrowsAsync<AiException>(async () => { await foreach (var _ in s.Chunks!) { } });

        var empty = Make(new FakeHandler(_ => Sse("{\"type\":\"message_stop\"}")));
        s = await empty.BeginDraftAsync(NewActor(), await SampleRequestAsync());
        await Assert.ThrowsAsync<AiException>(async () => { await foreach (var _ in s.Chunks!) { } });
    }

    [Fact]
    public async Task Draft_http_error_surfaces_when_enumerated()
    {
        var s = await Make(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }))
            .BeginDraftAsync(NewActor(), await SampleRequestAsync());
        Assert.True(s.Ok);
        var ex = await Assert.ThrowsAsync<AiException>(async () => { await foreach (var _ in s.Chunks!) { } });
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public void Sse_line_parser_handles_noise()
    {
        Assert.Null(AnthropicProvider.ParseSseLine("event: content_block_delta"));
        Assert.Null(AnthropicProvider.ParseSseLine(""));
        Assert.Null(AnthropicProvider.ParseSseLine("data: not json"));
        Assert.Null(AnthropicProvider.ParseSseLine("data: [DONE]"));
        Assert.Equal("x", AnthropicProvider.ParseSseLine("data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"x\"}}"));
    }

    [Fact]
    public async Task Summary_returns_text_and_logs_usage()
    {
        var actor = NewActor();
        var r = await Make(new FakeHandler(_ => Json("- Submitted\n- In progress"))).SummarizeTimelineAsync(actor, await SampleRequestAsync());
        Assert.True(r.Ok, r.Result.Message);
        Assert.StartsWith("- Submitted", r.Value);
        await using var db = await app.Db.CreateDbContextAsync();
        Assert.Equal("AI summary", (await db.AuditLogs.SingleAsync(a => a.ActorId == actor.UserId)).Action);
    }

    [Fact]
    public async Task Summary_refusal_is_reported()
    {
        var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"stop_reason\":\"refusal\",\"content\":[]}", Encoding.UTF8, "application/json") });
        var r = await Make(h).SummarizeTimelineAsync(NewActor(), await SampleRequestAsync());
        Assert.Equal(Failure.Unavailable, r.Result.Failure);
    }
}
