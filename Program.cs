using CivicOps.Web.Components;
using CivicOps.Web.Data;
using CivicOps.Web.Endpoints;
using CivicOps.Web.Services;
using CivicOps.Web.Theme;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Writable folder for the SQLite file and data-protection keys (App_Data is never overwritten by deployments).
var storagePath = builder.Configuration["Storage:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(storagePath);
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? $"Data Source={Path.Combine(storagePath, "civicops.db")}";

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddHealthChecks();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContextFactory<CivicOpsDb>(o => o.UseSqlite(connectionString));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<CivicOpsDb>>().CreateDbContext());
builder.Services.AddScoped<DemoDataSeeder>();
builder.Services.AddScoped<RequestQueryService>();
builder.Services.AddScoped<RequestCommandService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<AdminService>();

builder.Services.AddDataProtection()
    .SetApplicationName("CivicOps")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(storagePath, "keys")));

builder.Services.AddIdentityCore<ApplicationUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<CivicOpsDb>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<DisplayNameClaimsFactory>();

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies(o => { });
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/access-denied";
    o.Cookie.Name = "civicops.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
});
builder.Services.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser().Build());

// Behind IIS/ANCM (out-of-process) the original scheme arrives in X-Forwarded-Proto from 127.0.0.1.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    o.KnownProxies.Add(System.Net.IPAddress.Loopback);
    o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});
builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(30);
    o.IncludeSubDomains = false;
    o.Preload = false;
});

var app = builder.Build();

// Create the schema and seed demo data on startup (EnsureCreated: the demo has no migrations, see README).
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CivicOpsDb>>();
    await using (var db = await factory.CreateDbContextAsync())
        await db.Database.EnsureCreatedAsync();
    await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    // HTTP -> HTTPS is done here (the IIS URL Rewrite rule is unreliable on shared hosting). ACME HTTP-01 stays on plain HTTP.
    app.Use((ctx, next) =>
    {
        var req = ctx.Request;
        if (!req.IsHttps && !req.Path.StartsWithSegments("/.well-known/acme-challenge", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status301MovedPermanently;
            ctx.Response.Headers.Location = "https://" + req.Host + req.PathBase + req.Path + req.QueryString;
            return Task.CompletedTask;
        }
        return next();
    });
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.Use((ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["X-Frame-Options"] = "DENY";
    return next();
});
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapThemeEndpoint();
app.MapAccountEndpoints();
app.MapExportEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Exposed for WebApplicationFactory in tests/CivicOps.Web.Tests.
public partial class Program { }
