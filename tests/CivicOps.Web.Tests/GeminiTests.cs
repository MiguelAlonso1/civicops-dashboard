using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Web.Data;
using CivicOps.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CivicOps.Web.Tests;

/// <summary>Gemini provider and provider selection tests with fake handlers (no live calls).</summary>
public class GeminiTests(TestApp app) : IClassFixture<TestApp>
{
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Body;
        public HttpRequestMessage? Request;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static IConfiguration Cfg(params (string, string?)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(k => k.Item1, k => k.Item2)).Build();

    private static GeminiProvider Gemini(Fake h, string? key = "g-key", string? model = null) =>
        new(new Factory(h), Cfg(("Ai:Gemini:ApiKey", key), ("Ai:Gemini:Model", model)), NullLogger.Instance);

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } }, finishReason = "STOP" } } }), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Sse(params string[] data) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(data.Select(d => $"data: {d}\r\n\r\n")), Encoding.UTF8, "text/event-stream")
    };

    private static string Chunk(string text) => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });

    private static readonly AiPrompt P = new("sys text", "user text", 100);

    [Fact]
    public async Task Complete_sends_native_request_with_key_header_and_schema()
    {
        var h = new Fake(_ => Ok("{\"a\":1}"));
        var schema = new JsonObject { ["type"] = "object" };
        var text = await Gemini(h).CompleteAsync(P, schema, default);
        Assert.Equal("{\"a\":1}", text);
        Assert.Equal($"https://generativelanguage.googleapis.com/v1beta/models/{GeminiProvider.DefaultModel}:generateContent", h.Request!.RequestUri!.ToString());
        Assert.Equal("g-key", h.Request.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("g-key", h.Request.RequestUri.ToString());
        using var doc = JsonDocument.Parse(h.Body!);
        var r = doc.RootElement;
        Assert.Equal("sys text", r.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("user text", r.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        var gen = r.GetProperty("generationConfig");
        Assert.Equal("application/json", gen.GetProperty("responseMimeType").GetString());
        Assert.Equal("object", gen.GetProperty("responseJsonSchema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Plain_completion_has_no_schema_and_model_is_configurable()
    {
        var h = new Fake(_ => Ok("hello"));
        Assert.Equal("hello", await Gemini(h, model: "gemini-test").CompleteAsync(P, null, default));
        Assert.Contains("/models/gemini-test:generateContent", h.Request!.RequestUri!.ToString());
        Assert.DoesNotContain("responseMimeType", h.Body);
    }

    [Fact]
    public async Task Thought_parts_are_skipped()
    {
        var body = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"hidden\",\"thought\":true},{\"text\":\"shown\"}]}}]}";
        var h = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        Assert.Equal("shown", await Gemini(h).CompleteAsync(P, null, default));
    }

    [Fact]
    public async Task Stream_uses_sse_endpoint_and_yields_chunks()
    {
        var h = new Fake(_ => Sse(Chunk("Hello "), "{\"usageMetadata\":{}}", Chunk("Resident.")));
        var sb = new StringBuilder();
        await foreach (var c in Gemini(h).StreamAsync(P, default)) sb.Append(c);
        Assert.Equal("Hello Resident.", sb.ToString());
        Assert.EndsWith(":streamGenerateContent?alt=sse", h.Request!.RequestUri!.ToString());
        Assert.Equal("g-key", h.Request.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task Stream_empty_or_blocked_throws_friendly_errors()
    {
        await Assert.ThrowsAsync<AiException>(async () => { await foreach (var _ in Gemini(new Fake(_ => Sse("{\"candidates\":[{\"finishReason\":\"STOP\"}]}"))).StreamAsync(P, default)) { } });
        var ex = await Assert.ThrowsAsync<AiException>(async () => { await foreach (var _ in Gemini(new Fake(_ => Sse("{\"candidates\":[{\"finishReason\":\"SAFETY\"}]}"))).StreamAsync(P, default)) { } });
        Assert.Contains("declined", ex.Message);
    }

    [Theory]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"PROHIBITED_CONTENT\"}]}")]
    public async Task Safety_blocks_are_friendly(string json)
    {
        var h = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini(h).CompleteAsync(P, null, default));
        Assert.Contains("declined", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "quota")]
    [InlineData(HttpStatusCode.Forbidden, "rejected")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "temporarily unavailable")]
    [InlineData(HttpStatusCode.BadRequest, "could not complete")]
    public async Task Http_errors_are_friendly_and_do_not_leak(HttpStatusCode code, string expected)
    {
        var h = new Fake(_ => new HttpResponseMessage(code) { Content = new StringContent("{\"error\":{\"message\":\"secret-detail\"}}") });
        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini(h).CompleteAsync(P, null, default));
        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("secret-detail", ex.Message);
    }

    [Fact]
    public async Task Invalid_key_400_is_reported_as_key_problem()
    {
        var h = new Fake(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"details\":[{\"reason\":\"API_KEY_INVALID\"}]}}") });
        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini(h).CompleteAsync(P, null, default));
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public async Task Network_failure_is_friendly()
    {
        var h = new Fake(_ => throw new HttpRequestException("boom"));
        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini(h).CompleteAsync(P, null, default));
        Assert.DoesNotContain("boom", ex.Message);
    }

    // ---- provider selection and config fallbacks ----

    [Fact]
    public void Gemini_is_the_default_provider()
    {
        var p = AiConfig.Create(new Factory(new Fake(_ => Ok(""))), Cfg(("Ai:Gemini:ApiKey", "k")), NullLogger.Instance);
        Assert.IsType<GeminiProvider>(p);
        Assert.True(p.IsConfigured);
        Assert.IsType<AnthropicProvider>(AiConfig.Create(new Factory(new Fake(_ => Ok(""))), Cfg(("Ai:Provider", "anthropic")), NullLogger.Instance));
        Assert.IsType<GeminiProvider>(AiConfig.Create(new Factory(new Fake(_ => Ok(""))), Cfg(("Ai:Provider", "Gemini")), NullLogger.Instance));
    }

    [Fact]
    public void Configuration_is_per_selected_provider()
    {
        var f = new Factory(new Fake(_ => Ok("")));
        // A Gemini key does not enable Anthropic and vice versa.
        Assert.False(AiConfig.Create(f, Cfg(("Ai:Provider", "Anthropic"), ("Ai:Gemini:ApiKey", "k")), NullLogger.Instance).IsConfigured);
        Assert.False(AiConfig.Create(f, Cfg(("Claude:ApiKey", "k")), NullLogger.Instance).IsConfigured);
        // Legacy Claude:* and new Ai:Anthropic:* both work.
        Assert.True(AiConfig.Create(f, Cfg(("Ai:Provider", "Anthropic"), ("Claude:ApiKey", "k")), NullLogger.Instance).IsConfigured);
        var p = AiConfig.Create(f, Cfg(("Ai:Provider", "Anthropic"), ("Ai:Anthropic:ApiKey", "k"), ("Ai:Anthropic:Model", "m1")), NullLogger.Instance);
        Assert.True(p.IsConfigured);
        Assert.Equal("m1", p.Model);
    }

    [Fact]
    public async Task Service_with_gemini_triages_and_audits_provider_and_daily_limit_falls_back()
    {
        await using var db = await app.Db.CreateDbContextAsync();
        var cat = (await db.Categories.OrderBy(c => c.Id).FirstAsync(c => c.IsActive)).Name;
        var json = JsonSerializer.Serialize(new { category = cat, priority = "High", district = "NONE", summary = "s", rationale = "r" });
        var h = new Fake(_ => Ok(json));
        var cfg = Cfg(("Ai:Gemini:ApiKey", "g-key"), ("Ai:DailyLimit", "100000"));
        var ai = new AiService(app.Db, new Factory(h), cfg, TimeProvider.System, NullLogger<AiService>.Instance);
        Assert.Equal("Gemini", ai.ProviderName);
        var actor = new Actor("gem-" + Guid.NewGuid().ToString("N"), "Gem", Roles.Agent);
        var r = await ai.SuggestTriageAsync(actor, "Leak", "Water");
        Assert.True(r.Ok, r.Result.Message);
        Assert.Contains("generateContent", h.Request!.RequestUri!.ToString());
        var row = await db.AuditLogs.SingleAsync(a => a.ActorId == actor.UserId);
        Assert.Contains("Gemini", row.Details);
    }

    [Fact]
    public async Task Service_without_gemini_key_is_disabled_with_generic_message()
    {
        var h = new Fake(_ => Ok("{}"));
        var ai = new AiService(app.Db, new Factory(h), Cfg(("Claude:ApiKey", "only-anthropic")), TimeProvider.System, NullLogger<AiService>.Instance);
        Assert.False(ai.IsConfigured);
        var r = await ai.SuggestTriageAsync(new Actor("x-" + Guid.NewGuid().ToString("N"), "X", Roles.Agent), "Leak", "Water");
        Assert.Equal(Failure.Unavailable, r.Result.Failure);
        Assert.Contains("API key", r.Result.Message);
        Assert.Null(h.Request);
    }
}
