using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CivicOps.Web.Data;

/// <summary>
/// The demo uses EnsureCreated (no migrations), which never alters existing tables. The schema version is stored in
/// SQLite's <c>PRAGMA user_version</c>; when it differs from <see cref="Current"/> the database is dropped, recreated and
/// reseeded (demo data only, so nothing of value is lost). Bump <see cref="Current"/> on every model change.
/// </summary>
public static class SchemaVersion
{
    /// <summary>1 = original schema (user_version 0), 2 = adds AuditLogs and RequestEvents.ActorId/EditedAt.</summary>
    public const int Current = 2;

    public static async Task EnsureAsync(IDbContextFactory<CivicOpsDb> factory, ILogger logger, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var version = await db.Database.SqlQueryRaw<int>("SELECT user_version AS Value FROM pragma_user_version").SingleAsync(ct);
        var hasTables = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table'").SingleAsync(ct) > 0;

        if (hasTables && version != Current)
        {
            logger.LogWarning("Database schema version {Found} does not match {Expected}: recreating and reseeding the demo database (demo data only).", version, Current);
            await db.Database.CloseConnectionAsync();
            SqliteConnection.ClearAllPools();
            await db.Database.EnsureDeletedAsync(ct);
        }

        await db.Database.EnsureCreatedAsync(ct);
        if (!hasTables || version != Current)
        {
            await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version = {Current}", ct);
            logger.LogInformation("Database schema version is now {Version}.", Current);
        }
    }
}
