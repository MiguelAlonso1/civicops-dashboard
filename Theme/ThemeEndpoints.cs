namespace CivicOps.Web.Theme;

public static class ThemeEndpoints
{
    /// <summary>GET /theme/{mode}?returnUrl=/ stores the explicit choice in a cookie, then redirects (local URLs only).</summary>
    public static void MapThemeEndpoint(this WebApplication app) =>
        app.MapGet("/theme/{mode}", (string mode, string? returnUrl, HttpContext ctx) =>
        {
            if (mode is "light" or "dark")
            {
                ctx.Response.Cookies.Append(AppTheme.CookieName, mode, new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    Path = "/",
                    SameSite = SameSiteMode.Lax,
                    Secure = ctx.Request.IsHttps,
                    IsEssential = true
                });
            }
            var target = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/')
                && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\") ? returnUrl : "/";
            return Results.LocalRedirect(target);
        }).ExcludeFromDescription();
}
