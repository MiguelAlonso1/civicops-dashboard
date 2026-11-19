using System.Net;
using CivicOps.Web.Data;
using CivicOps.Web.Endpoints;

namespace CivicOps.Web.Tests;

public class HttpTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Health_is_public()
    {
        var res = await app.NewClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/requests")]
    [InlineData("/requests/1")]
    [InlineData("/districts")]
    [InlineData("/admin")]
    [InlineData("/api/requests/export.csv")]
    public async Task Anonymous_requests_are_sent_to_login(string path)
    {
        var res = await app.NewClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/login", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Login_page_is_public()
    {
        var res = await app.NewClient().GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Try the demo", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Theme_toggle_is_a_plain_link_with_return_url()
    {
        var html = await (await app.NewClient().GetAsync("/login")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/theme/light?returnUrl=%2Flogin\"", html);
        Assert.Contains("data-enhance-nav=\"false\"", html);
    }

    [Fact]
    public async Task Demo_login_without_antiforgery_token_is_rejected()
    {
        var res = await app.NewClient().PostAsync("/account/demo", new FormUrlEncodedContent(new Dictionary<string, string> { ["role"] = "Admin" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData(Roles.Admin, HttpStatusCode.OK)]
    [InlineData(Roles.Supervisor, HttpStatusCode.Redirect)]
    [InlineData(Roles.Agent, HttpStatusCode.Redirect)]
    [InlineData(Roles.Viewer, HttpStatusCode.Redirect)]
    public async Task Admin_page_is_admin_only(string role, HttpStatusCode expected)
    {
        var c = await app.SignInAsync(role);
        var res = await c.GetAsync("/admin");
        Assert.Equal(expected, res.StatusCode);
        if (expected == HttpStatusCode.Redirect) Assert.Contains("/access-denied", res.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Viewer)]
    public async Task Signed_in_users_can_open_dashboard_and_requests(string role)
    {
        var c = await app.SignInAsync(role);
        foreach (var path in new[] { "/", "/requests", "/requests/1", "/districts" })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Csv_export_returns_filtered_rows_as_a_download()
    {
        var c = await app.SignInAsync(Roles.Viewer);
        var res = await c.GetAsync("/api/requests/export.csv?status=Closed&sort=Number&desc=false");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType!.MediaType);
        Assert.Contains("attachment", res.Content.Headers.ContentDisposition!.ToString());
        var lines = (await res.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("﻿Number,Title,Category", lines[0]);
        Assert.True(lines.Length > 10);
        Assert.All(lines.Skip(1), l => Assert.Contains(",Closed,", l));
    }

    [Fact]
    public async Task Logout_clears_the_session()
    {
        var c = await app.SignInAsync(Roles.Agent);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/requests")).StatusCode);
        await c.PostAsync("/account/logout", null);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/requests")).StatusCode);
    }

    [Fact]
    public void Unsafe_return_urls_are_ignored()
    {
        Assert.Equal("/", AccountEndpoints.SafeReturnUrl("https://evil.example"));
        Assert.Equal("/", AccountEndpoints.SafeReturnUrl("//evil.example"));
        Assert.Equal("/requests?x=1", AccountEndpoints.SafeReturnUrl("/requests?x=1"));
    }
}

public class HttpsRedirectTests(TestApp app) : IClassFixture<TestApp>
{
    private HttpClient Client(bool redirect = true) => app
        .WithWebHostBuilder(b => b.UseSetting("Https:Redirect", redirect ? "true" : "false"))
        .CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://ops.alonsomiguel.com")
        });

    [Fact]
    public async Task Http_RedirectsToHttps_PreservingPathAndQuery()
    {
        var res = await Client().GetAsync("/requests?x=1");
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.Equal("https://ops.alonsomiguel.com/requests?x=1", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AcmeChallengeOverHttp_IsNotRedirected()
    {
        var res = await Client().GetAsync("/.well-known/acme-challenge/x");
        Assert.NotEqual(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.DoesNotContain("https://", res.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task Http_IsServedWhenRedirectIsOff()
    {
        var res = await Client(redirect: false).GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
