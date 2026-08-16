using Microsoft.EntityFrameworkCore;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Tables;

/// <summary>
/// Job listing plus the cancel/retry UX that roadmap.md calls "the single biggest thing to get
/// right" for this phase. Cancellation of a running job is best-effort via <c>pg_cancel_backend</c>
/// on the pid the runner captured; the runner itself observes the resulting cancellation and
/// finalizes the job and its target resource, so this service never touches DDL directly.
/// </summary>
public sealed class SchemaJobsService(PraxyDb db, NpgsqlDataSource dataSource, CatalogCache cache)
{
    public Task<List<SchemaJob>> ListAsync(Guid databaseId, Guid? tableId, CancellationToken ct)
    {
        var query = db.SchemaJobs.Where(j => j.DatabaseId == databaseId);
        if (tableId is { } t)
            query = query.Where(j => j.TableId == t);
        return query.OrderByDescending(j => j.CreatedAt).ToListAsync(ct);
    }

    public async Task<SchemaJob> GetAsync(Guid databaseId, Guid jobId, CancellationToken ct) =>
        await db.SchemaJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.DatabaseId == databaseId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.SchemaJobNotFound, "Job not found.");

    public async Task<SchemaJob> CancelAsync(SchemaJob job, CancellationToken ct)
    {
        switch (job.Status)
        {
            case "queued":
                job.Status = "cancelled";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await MarkTargetAsync(db, job, "failed", "Cancelled before it started.", cache, ct);
                await db.SaveChangesAsync(ct);
                return job;

            case "processing":
                // Best-effort: the runner's own DDL call will surface the resulting
                // query_canceled error and finalize status/cleanup — this just requests it.
                if (job.Pid is { } pid)
                {
                    await using var conn = await dataSource.OpenConnectionAsync(ct);
                    await using var cmd = new NpgsqlCommand("SELECT pg_cancel_backend($1)", conn);
                    cmd.Parameters.AddWithValue(pid);
                    await cmd.ExecuteScalarAsync(ct);
                }
                return job;

            default:
                throw new PraxyException(400, ErrorTypes.SchemaJobInvalidState,
                    $"Only queued or processing jobs can be cancelled (this one is '{job.Status}').");
        }
    }

    public async Task<SchemaJob> RetryAsync(SchemaJob job, CancellationToken ct)
    {
        if (job.Status is not ("failed" or "cancelled"))
            throw new PraxyException(400, ErrorTypes.SchemaJobInvalidState,
                $"Only failed or cancelled jobs can be retried (this one is '{job.Status}').");

        job.Status = "queued";
        job.Error = null;
        job.Pid = null;
        job.Attempts += 1;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await MarkTargetAsync(db, job, "processing", null, cache, ct);
        await db.SaveChangesAsync(ct);
        return job;
    }

    /// <summary>
    /// Flips the job's target resource (today: always an index) alongside the job itself. Static
    /// and keyed on the payload so the runner (which owns a differently-scoped <see cref="PraxyDb"/>)
    /// can call the exact same logic when it finalizes a job. Also invalidates the owning table's
    /// catalog cache entry — an index flipping to available/failed changes what the query compiler
    /// (e.g. <c>search</c>'s fulltext-index requirement) sees for that table.
    /// </summary>
    public static async Task MarkTargetAsync(
        PraxyDb db, SchemaJob job, string status, string? error, CatalogCache cache, CancellationToken ct)
    {
        if (job.TableId is { } tableId)
            cache.Invalidate(tableId);
        if (job.Kind != SchemaJobKinds.CreateIndex)
            return;
        var payload = System.Text.Json.JsonSerializer.Deserialize<CreateIndexJobPayload>(job.Payload)
            ?? throw new InvalidOperationException("Malformed create_index job payload.");
        if (!Ids.TryParseWire(payload.IndexId, out var indexId))
            return;
        await db.Indexes.Where(i => i.Id == indexId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, status)
                .SetProperty(i => i.Error, error)
                .SetProperty(i => i.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }
}
