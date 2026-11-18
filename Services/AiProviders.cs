using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CivicOps.Web.Services;

/// <summary>A provider-agnostic prompt: frozen system text, user text and an output token budget.</summary>
public sealed record AiPrompt(string System, string User, int MaxTokens);

/// <summary>A model backend. Implementations throw <see cref="AiException"/> with a friendly message; they never see roles, limits or audit.</summary>
public interface IAiProvider
{
    /// <summary>Provider name shown in the audit log, e.g. "Gemini".</summary>
    string Name { get; }
    string Model { get; }
    bool IsConfigured { get; }
    /// <summary>Single completion. With a JSON schema the returned text is JSON constrained to it.</summary>
    Task<string> CompleteAsync(AiPrompt prompt, JsonObject? jsonSchema, CancellationToken ct);
    /// <summary>Streams text deltas.</summary>
    IAsyncEnumerable<string> StreamAsync(AiPrompt prompt, CancellationToken ct);
}

/// <summary>Reads provider-agnostic AI settings with fallback to the older Claude:* keys, and picks the provider.</summary>
public static class AiConfig
{
    public static string? Get(IConfiguration c, string key, string? legacyKey = null)
    {
        var v = c[key];
        if (string.IsNullOrWhiteSpace(v) && legacyKey is not null) v = c[legacyKey];
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    public static int GetInt(IConfiguration c, string key, string legacyKey, int fallback) =>
        int.TryParse(Get(c, key, legacyKey), out var n) && n > 0 ? n : fallback;

    public static bool UseAnthropic(IConfiguration c) =>
        string.Equals(Get(c, "Ai:Provider"), "Anthropic", StringComparison.OrdinalIgnoreCase);

    public static IAiProvider Create(IHttpClientFactory http, IConfiguration c, ILogger log) =>
        UseAnthropic(c) ? new AnthropicProvider(http, c, log) : new GeminiProvider(http, c, log);
}

/// <summary>Shared HTTP plumbing: timeouts, network and JSON failures mapped to friendly errors.</summary>
public abstract class HttpAiProvider(IHttpClientFactory http, IConfiguration config, ILogger log) : IAiProvider
{
    public abstract string Name { get; }
    public abstract string Model { get; }
    public abstract bool IsConfigured { get; }
    protected abstract HttpRequestMessage NewRequest(AiPrompt prompt, JsonObject? schema, bool stream);
    protected abstract string ExtractText(JsonElement root);
    /// <summary>Text delta carried by one SSE line, or null. May throw <see cref="AiException"/>.</summary>
    protected abstract string? ParseStreamLine(string line);
    protected abstract AiException Friendly(HttpStatusCode code, string body);

    protected IConfiguration Config => config;
    protected ILogger Log => log;
    private int TimeoutSeconds => AiConfig.GetInt(config, "Ai:TimeoutSeconds", "Claude:TimeoutSeconds", 30);

    public async Task<string> CompleteAsync(AiPrompt prompt, JsonObject? jsonSchema, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            using var res = await http.CreateClient(AiService.HttpClientName).SendAsync(NewRequest(prompt, jsonSchema, false), cts.Token);
            if (!res.IsSuccessStatusCode) throw Friendly(res.StatusCode, await res.Content.ReadAsStringAsync(cts.Token));
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            return ExtractText(doc.RootElement);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AiException("The AI service timed out. Try again."); }
        catch (HttpRequestException ex) { log.LogWarning(ex, "{Provider} request failed", Name); throw new AiException("Could not reach the AI service. Try again later."); }
        catch (JsonException ex) { log.LogWarning(ex, "{Provider} returned invalid JSON", Name); throw new AiException("The AI service returned an unreadable answer. Try again."); }
    }

    public async IAsyncEnumerable<string> StreamAsync(AiPrompt prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds * 3));
        HttpResponseMessage res;
        try { res = await http.CreateClient(AiService.HttpClientName).SendAsync(NewRequest(prompt, null, true), HttpCompletionOption.ResponseHeadersRead, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AiException("The AI service timed out. Try again."); }
        catch (HttpRequestException ex) { log.LogWarning(ex, "{Provider} request failed", Name); throw new AiException("Could not reach the AI service. Try again later."); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw Friendly(res.StatusCode, await res.Content.ReadAsStringAsync(cts.Token));
            using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
            var any = false;
            while (true)
            {
                string? line;
                try { line = await reader.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AiException("The AI service timed out. Try again."); }
                if (line is null) break;
                var chunk = ParseStreamLine(line);
                if (string.IsNullOrEmpty(chunk)) continue;
                any = true;
                yield return chunk;
            }
            if (!any) throw new AiException("The AI returned an empty answer. Try again.");
        }
    }

    protected static string? SseData(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var json = line[5..].Trim();
        return json.Length == 0 || json == "[DONE]" ? null : json;
    }

    protected static string Clip(string body) => body.Length > 300 ? body[..300] : body;
}

/// <summary>Anthropic Messages API (no SDK). Config: Ai:Anthropic:* with fallback to the original Claude:* keys.</summary>
public sealed class AnthropicProvider(IHttpClientFactory http, IConfiguration config, ILogger log) : HttpAiProvider(http, config, log)
{
    public const string DefaultModel = "claude-haiku-5-5";
    public override string Name => "Claude";
    private string? ApiKey => AiConfig.Get(Config, "Ai:Anthropic:ApiKey", "Claude:ApiKey");
    public override bool IsConfigured => ApiKey is not null;
    public override string Model => AiConfig.Get(Config, "Ai:Anthropic:Model", "Claude:Model") ?? DefaultModel;
    private string Endpoint => AiConfig.Get(Config, "Ai:Anthropic:BaseUrl", "Claude:BaseUrl") is { } b ? b.TrimEnd('/') + "/v1/messages" : "https://api.anthropic.com/v1/messages";

    protected override HttpRequestMessage NewRequest(AiPrompt prompt, JsonObject? schema, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = prompt.MaxTokens,
            // The system prompt is frozen text, so it is cached (prefix match; prompts below the model minimum are simply not cached).
            ["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt.System, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } }),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt.User }),
        };
        var oc = new JsonObject { ["effort"] = "low" };
        if (schema is not null) oc["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = schema.DeepClone() };
        body["output_config"] = oc;
        if (stream) body["stream"] = true;
        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-api-key", ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        return req;
    }

    protected override string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("stop_reason", out var sr) && sr.GetString() == "refusal")
            throw new AiException("The AI declined to answer this request.");
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new AiException("The AI service returned an unexpected answer. Try again.");
        var sb = new StringBuilder();
        foreach (var b in content.EnumerateArray())
            if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var tx)) sb.Append(tx.GetString());
        return sb.ToString();
    }

    protected override string? ParseStreamLine(string line) => ParseSseLine(line);

    /// <summary>Returns the text delta carried by one SSE line, or null for anything else. Throws on an in-stream error event.</summary>
    public static string? ParseSseLine(string line)
    {
        var json = SseData(line);
        if (json is null) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "error") throw new AiException("The AI service reported an error. Try again.");
            if (type == "content_block_delta" && root.TryGetProperty("delta", out var d)
                && d.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta" && d.TryGetProperty("text", out var tx))
                return tx.GetString();
        }
        return null;
    }

    protected override AiException Friendly(HttpStatusCode code, string body)
    {
        Log.LogWarning("Claude API returned {Status}: {Body}", (int)code, Clip(body));
        return new AiException(code switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The AI service rejected the API key. Ask an administrator to check it.",
            HttpStatusCode.TooManyRequests => "The AI service is busy right now. Try again in a minute.",
            (HttpStatusCode)529 => "The AI service is overloaded. Try again in a minute.",
            _ => "The AI service could not complete the request. Try again later."
        });
    }
}

/// <summary>
/// Google Gemini API (free tier capable), native generateContent / streamGenerateContent?alt=sse over HttpClient.
/// Key in the x-goog-api-key header. Config: Ai:Gemini:ApiKey, Ai:Gemini:Model, Ai:Gemini:BaseUrl.
/// </summary>
public sealed class GeminiProvider(IHttpClientFactory http, IConfiguration config, ILogger log) : HttpAiProvider(http, config, log)
{
    public const string DefaultModel = "gemini-3.5-flash-lite";
    public override string Name => "Gemini";
    private string? ApiKey => AiConfig.Get(Config, "Ai:Gemini:ApiKey");
    public override bool IsConfigured => ApiKey is not null;
    public override string Model => AiConfig.Get(Config, "Ai:Gemini:Model") ?? DefaultModel;
    private string BaseUrl => (AiConfig.Get(Config, "Ai:Gemini:BaseUrl") ?? "https://generativelanguage.googleapis.com/v1beta").TrimEnd('/');

    protected override HttpRequestMessage NewRequest(AiPrompt prompt, JsonObject? schema, bool stream)
    {
        var gen = new JsonObject { ["maxOutputTokens"] = prompt.MaxTokens * 2, ["temperature"] = schema is null ? 0.7 : 0.2 };
        if (schema is not null)
        {
            gen["responseMimeType"] = "application/json";
            gen["responseJsonSchema"] = schema.DeepClone();
        }
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt.System }) },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt.User }) }),
            ["generationConfig"] = gen,
        };
        var url = $"{BaseUrl}/models/{Uri.EscapeDataString(Model)}:" + (stream ? "streamGenerateContent?alt=sse" : "generateContent");
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-goog-api-key", ApiKey);
        return req;
    }

    private static readonly HashSet<string> BlockedFinish = ["SAFETY", "RECITATION", "BLOCKLIST", "PROHIBITED_CONTENT", "SPII", "IMAGE_SAFETY", "LANGUAGE"];

    /// <summary>Text of one GenerateContentResponse (a full reply or one stream chunk). Throws when the prompt or answer was blocked.</summary>
    protected override string ExtractText(JsonElement root)
    {
        const string declined = "The AI declined to answer this request.";
        if (root.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out var br) && br.ValueKind == JsonValueKind.String)
            throw new AiException(declined);
        if (!root.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
            throw new AiException("The AI service returned an unexpected answer. Try again.");
        var cand = cands[0];
        var sb = new StringBuilder();
        if (cand.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            foreach (var p in parts.EnumerateArray())
            {
                if (p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True) continue;
                if (p.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) sb.Append(tx.GetString());
            }
        if (sb.Length == 0 && cand.TryGetProperty("finishReason", out var fr) && fr.GetString() is { } f && BlockedFinish.Contains(f))
            throw new AiException(declined);
        return sb.ToString();
    }

    protected override string? ParseStreamLine(string line)
    {
        var json = SseData(line);
        if (json is null) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (doc)
        {
            if (doc.RootElement.TryGetProperty("error", out _)) throw new AiException("The AI service reported an error. Try again.");
            if (!doc.RootElement.TryGetProperty("candidates", out _) && !doc.RootElement.TryGetProperty("promptFeedback", out _)) return null;
            return ExtractText(doc.RootElement);
        }
    }

    protected override AiException Friendly(HttpStatusCode code, string body)
    {
        Log.LogWarning("Gemini API returned {Status}: {Body}", (int)code, Clip(body));
        var keyInvalid = code == HttpStatusCode.BadRequest && body.Contains("API_KEY_INVALID", StringComparison.Ordinal);
        if (keyInvalid) return new AiException("The AI service rejected the API key. Ask an administrator to check it.");
        return new AiException(code switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The AI service rejected the API key or access is not allowed. Ask an administrator to check it.",
            HttpStatusCode.TooManyRequests => "The free AI quota is used up or the service is busy. Try again in a minute.",
            >= HttpStatusCode.InternalServerError => "The AI service is temporarily unavailable. Try again in a minute.",
            _ => "The AI service could not complete the request. Try again later."
        });
    }
}
