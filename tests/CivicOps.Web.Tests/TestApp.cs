using System.Net;
using System.Text.RegularExpressions;
using CivicOps.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Web.Tests;

/// <summary>Boots the real app against a shared in-memory SQLite database (seeded on startup).</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string _cs = $"Data Source=civicops-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "civicops-tests-" + Guid.NewGuid().ToString("N"));
    private SqliteConnection? _keepAlive;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _keepAlive = new SqliteConnection(_cs);
        _keepAlive.Open(); // an in-memory database lives only while a connection is open
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _cs);
        builder.UseSetting("Storage:Path", _storage);
        // Isolate from a developer's user-secrets / env vars: tests set a fake key explicitly when needed.
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "Gemini",
            ["Ai:Gemini:ApiKey"] = "",
            ["Ai:Anthropic:ApiKey"] = "",
            ["Claude:ApiKey"] = ""
        }));
    }

    public IDbContextFactory<CivicOpsDb> Db => Services.GetRequiredService<IDbContextFactory<CivicOpsDb>>();

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });

    /// <summary>Signs in through the real one-click demo form post (antiforgery token and cookie included).</summary>
    public async Task<HttpClient> SignInAsync(string role)
    {
        var c = NewClient();
        var page = await c.GetStringAsync("/login");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        var res = await c.PostAsync("/account/demo", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["role"] = role, ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive?.Dispose();
            try { Directory.Delete(_storage, true); } catch (IOException) { }
        }
    }
}
