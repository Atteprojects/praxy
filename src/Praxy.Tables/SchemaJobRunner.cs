using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Tables;

public sealed record SchemaJobRunnerOptions(int PollIntervalSeconds = 2, int IndexBuildTimeoutSeconds = 1800);

/// <summary>
/// Consumes <c>praxy.schema_jobs</c> with <c>FOR UPDATE SKIP LOCKED</c>. A single worker loop —
/// which trivially satisfies architecture.md §4.5's "serialized per database" (global
/// serialization is a stricter special case of it) at a real simplicity win for a self-hosted v1;
/// scaling to N workers with a per-database advisory lock is the natural v1.1 step if job volume
/// ever warrants it. This is the roadmap's named "beat Appwrite" feature: elapsed time, cancel,
/// and retry all work off the same job row the console reads.
/// </summary>
public sealed class SchemaJobRunner(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopeFactory,
    SchemaJobSignal signal,
    SchemaJobRunnerOptions options,
    CatalogCache cache,
    ILogger<SchemaJobRunner> logger) : BackgroundService
{
    /// <summary>Postgres' <c>query_canceled</c> SQL state — what a running statement gets after <c>pg_cancel_backend</c>.</summary>
    private const string QueryCanceledSqlState = "57014";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Single-runner deployment assumption (true for Praxy's self-host default): any job left
        // 'processing' after an ungraceful shutdown has no other node that could be running it,
        // so it's safe to requeue rather than leave it stuck forever.
        await ResetStuckJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

            SchemaJob? job;
            try
            {
                job = await ClaimNextAsync(db, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Schema job claim failed; backing off");
                job = null;
            }

            if (job is null)
            {
                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                await ExecuteJobAsync(db, job, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Schema job {JobId} failed unexpectedly outside its own handling", job.Id);
            }
        }
    }

    private async Task ResetStuckJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        var reset = await db.SchemaJobs
            .Where(j => j.Status == "processing")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "queued")
                .SetProperty(j => j.Pid, (int?)null)
                .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (reset > 0)
            logger.LogWarning("Requeued {Count} schema job(s) left 'processing' from a previous run", reset);
    }

    private static async Task<SchemaJob?> ClaimNextAsync(PraxyDb db, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        const string sql = """
            UPDATE praxy.schema_jobs
            SET status = 'processing', updated_at = now()
            WHERE id = (
                SELECT id FROM praxy.schema_jobs
                WHERE status = 'queued'
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING id, database_id, table_id, kind, payload, status, attempts, error, pid, created_at, updated_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new SchemaJob
        {
            Id = reader.GetGuid(0),
            DatabaseId = reader.GetGuid(1),
            TableId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
            Kind = reader.GetString(3),
            Payload = reader.GetString(4),
            Status = reader.GetString(5),
            Attempts = reader.GetInt32(6),
            Error = reader.IsDBNull(7) ? null : reader.GetString(7),
            Pid = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(9),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(10),
        };
    }

    private Task ExecuteJobAsync(PraxyDb db, SchemaJob job, CancellationToken ct) => job.Kind switch
    {
        SchemaJobKinds.CreateIndex => ExecuteCreateIndexAsync(db, job, ct),
        _ => FinalizeAsync(db, job, "failed", $"Unknown job kind '{job.Kind}'.", ct),
    };

    private async Task ExecuteCreateIndexAsync(PraxyDb db, SchemaJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<CreateIndexJobPayload>(job.Payload)
            ?? throw new InvalidOperationException("Malformed create_index job payload.");
        var qualifiedTable = PhysicalNaming.QualifiedTable(payload.SchemaName, payload.TablePhysicalName);
        var indexName = PhysicalNaming.Quote(payload.IndexPhysicalName);

        // CREATE INDEX CONCURRENTLY cannot run inside any transaction, so this DDL always uses a
        // fresh, autocommit connection straight from the pool — never the EF-managed one.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var pid = (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conn).ExecuteScalarAsync(ct))!;
        await db.SchemaJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Pid, pid), ct);

        try
        {
            await ExecAsync(conn, "SET lock_timeout = '5s'", ct);
            await ExecAsync(conn, $"SET statement_timeout = '{options.IndexBuildTimeoutSeconds}s'", ct);

            if (payload.Fulltext)
            {
                var ftsColumn = PhysicalNaming.Quote(payload.FulltextColumnPhysical!);
                var expr = string.Join(" || ' ' || ",
                    payload.ColumnsPhysical.Select(c => $"coalesce({PhysicalNaming.Quote(c)}, '')"));
                await ExecAsync(conn,
                    $"ALTER TABLE {qualifiedTable} ADD COLUMN IF NOT EXISTS {ftsColumn} tsvector " +
                    $"GENERATED ALWAYS AS (to_tsvector('simple', {expr})) STORED", ct);
                await ExecAsync(conn,
                    $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {indexName} ON {qualifiedTable} USING GIN ({ftsColumn})", ct);
            }
            else
            {
                var uniqueKeyword = payload.Unique ? "UNIQUE " : "";
                var columnList = string.Join(", ", payload.ColumnsPhysical.Zip(payload.Orders,
                    (col, order) => $"{PhysicalNaming.Quote(col)} {(order == "desc" ? "DESC" : "ASC")}"));
                await ExecAsync(conn,
                    $"CREATE {uniqueKeyword}INDEX CONCURRENTLY IF NOT EXISTS {indexName} ON {qualifiedTable} ({columnList})", ct);
            }

            await FinalizeAsync(db, job, "available", null, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == QueryCanceledSqlState)
        {
            await CleanupInvalidIndexAsync(payload, ct);
            await FinalizeAsync(db, job, "cancelled", "Cancelled by user request.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CleanupInvalidIndexAsync(payload, ct);
            await FinalizeAsync(db, job, "failed", ex.Message, ct);
        }
    }

    /// <summary>
    /// A cancelled or failed <c>CREATE INDEX CONCURRENTLY</c> leaves an <c>INVALID</c> index object
    /// behind that would collide with a retry — drop it. Best-effort: a failure here just means the
    /// next retry attempt does it again (it uses <c>IF EXISTS</c> too).
    /// </summary>
    private async Task CleanupInvalidIndexAsync(CreateIndexJobPayload payload, CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await ExecAsync(conn, "SET lock_timeout = '5s'", ct);
            var qualifiedIndex = $"{PhysicalNaming.Quote(payload.SchemaName)}.{PhysicalNaming.Quote(payload.IndexPhysicalName)}";
            await ExecAsync(conn, $"DROP INDEX CONCURRENTLY IF EXISTS {qualifiedIndex}", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clean up an invalid index after job failure; a retry will attempt it again");
        }
    }

    private async Task FinalizeAsync(PraxyDb db, SchemaJob job, string jobStatus, string? error, CancellationToken ct)
    {
        await db.SchemaJobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, jobStatus)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.Pid, (int?)null)
                .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);

        // The target resource (an index today) has no "cancelled" state of its own — both
        // outcomes surface to it as "failed", with the job's own status/detail staying distinct.
        var targetStatus = jobStatus == "available" ? "available" : "failed";
        await SchemaJobsService.MarkTargetAsync(db, job, targetStatus, error, cache, ct);
    }

    private static Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct) =>
        new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync(ct);
}
