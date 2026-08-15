using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Praxy.Core;

namespace Praxy.Persistence;

/// <summary>
/// Applies catalog migrations at startup under a session-level advisory lock so
/// multi-node rollouts never race. Session-level, not transaction-level: EF migrations
/// span multiple transactions, so <c>pg_advisory_xact_lock</c> would release too early.
/// </summary>
public static class CatalogMigrator
{
    /// <summary>"PRAXY" in ASCII hex — the cluster-wide migration lock key.</summary>
    public const long MigrationLockKey = 0x5052415859;

    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Praxy.CatalogMigrator");

        // One dedicated connection holds the lock for the whole migration.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock($1)", conn))
        {
            acquire.Parameters.AddWithValue(MigrationLockKey);
            await acquire.ExecuteNonQueryAsync(ct);
        }

        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} catalog migration(s): {Migrations}", pending.Count, pending);
                await db.Database.MigrateAsync(ct);
            }
            else
            {
                logger.LogInformation("Catalog is up to date");
            }

            await SeedConsoleProjectAsync(db, ct);
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", conn);
            release.Parameters.AddWithValue(MigrationLockKey);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>The reserved console project row. Idempotent; runs under the same lock as migrations.</summary>
    private static async Task SeedConsoleProjectAsync(PraxyDb db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $$"""
             INSERT INTO praxy.projects (id, organization_id, name, settings, created_at, updated_at)
             VALUES ({{Ids.ConsoleProjectId}}, NULL, 'Console', '{}'::jsonb, now(), now())
             ON CONFLICT (id) DO NOTHING
             """,
            ct);
    }
}
