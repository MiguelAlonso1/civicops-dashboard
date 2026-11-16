using System.Security.Claims;
using CivicOps.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CivicOps.Web.Endpoints;

/// <summary>Adds the display name to the auth cookie so the UI and the audit trail can show it without a lookup.</summary>
public sealed class DisplayNameClaimsFactory(
    UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles, Microsoft.Extensions.Options.IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        id.AddClaim(new Claim(Services.Actor.DisplayNameClaim, user.DisplayName));
        return id;
    }
}

public static class AccountEndpoints
{
    /// <summary>Only same-site relative paths are allowed as post-login destinations.</summary>
    public static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    public static void MapAccountEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/account");

        // Plain HTML form posts (no JavaScript, no SignalR): antiforgery tokens come from <AntiforgeryToken /> on the login page.
        g.MapPost("/login", async ([FromForm] string? email, [FromForm] string? password, [FromForm] string? returnUrl,
            SignInManager<ApplicationUser> signIn) =>
        {
            var ret = Uri.EscapeDataString(SafeReturnUrl(returnUrl));
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
                return Results.LocalRedirect($"/login?error=1&returnUrl={ret}");
            var result = await signIn.PasswordSignInAsync(email.Trim(), password, isPersistent: false, lockoutOnFailure: true);
            if (result.Succeeded) return Results.LocalRedirect(SafeReturnUrl(returnUrl));
            return Results.LocalRedirect($"/login?error={(result.IsLockedOut ? 2 : 1)}&returnUrl={ret}");
        }).AllowAnonymous();

        // One-click demo sign-in. Public demo only: disable with Demo:OneClickLogins=false.
        g.MapPost("/demo", async ([FromForm] string? role, [FromForm] string? returnUrl,
            SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users, IConfiguration cfg) =>
        {
            if (!cfg.GetValue("Demo:OneClickLogins", true)) return Results.NotFound();
            if (role is null || !DemoDataSeeder.DemoLogins.TryGetValue(role, out var demo)) return Results.BadRequest();
            var user = await users.FindByEmailAsync(demo.Email);
            if (user is null) return Results.NotFound();
            if (await users.IsLockedOutAsync(user)) return Results.LocalRedirect("/login?error=2");
            await signIn.SignInAsync(user, isPersistent: false);
            return Results.LocalRedirect(SafeReturnUrl(returnUrl));
        }).AllowAnonymous();

        // Signing out only drops the caller's own cookie, so no antiforgery token is required (it is posted from the interactive layout).
        g.MapPost("/logout", async (SignInManager<ApplicationUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/login");
        }).AllowAnonymous().DisableAntiforgery();
    }
}
