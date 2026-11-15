using CivicOps.Web.Services;

namespace CivicOps.Web.Endpoints;

public static class ExportEndpoints
{
    /// <summary>GET /api/requests/export.csv?search=&amp;status=... downloads every request matching the filters (no paging).</summary>
    public static void MapExportEndpoints(this WebApplication app) =>
        app.MapGet("/api/requests/export.csv", async (HttpContext ctx, RequestQueryService svc, TimeProvider clock, CancellationToken ct) =>
        {
            var q = RequestQuery.FromQuery(ctx.Request.Query);
            var rows = await svc.ExportAsync(q, Actor.From(ctx.User), ct);
            var csv = CsvExport.Build(rows, clock.GetUtcNow().UtcDateTime);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8",
                $"civicops-requests-{clock.GetUtcNow():yyyyMMdd}.csv");
        }).RequireAuthorization().ExcludeFromDescription();
}
