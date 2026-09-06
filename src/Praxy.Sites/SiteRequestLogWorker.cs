using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Sites;

/// <summary>
/// Drains <see cref="SiteRequestLogWriter"/>'s channel and batches inserts into
/// <c>praxy.site_requests</c> — the consumer half of the producer/consumer pair
/// <see cref="SiteProxyMiddleware"/> feeds. Not a claim-based worker like <c>FunctionExecutionWorker</c>
/// (there's no "pending work" row to claim; the request itself is the event, already in memory the
/// instant it's enqueued) — waits for at least one entry, then drains whatever else is immediately
/// available before flushing, so a burst of concurrent requests becomes one insert instead of many.
/// </summary>
public sealed class SiteRequestLogWorker(
    SiteRequestLogWriter writer, IServiceScopeFactory scopeFactory, ILogger<SiteRequestLogWorker> logger)
    : BackgroundService
{
    private const int MaxBatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = writer.Reader;
        while (await reader.WaitToReadAsync(stoppingToken))
        {
            var batch = new List<SiteRequestLog>();
            while (batch.Count < MaxBatchSize && reader.TryRead(out var entry))
            {
                batch.Add(new SiteRequestLog
                {
                    Id = Ids.NewUuid(),
                    SiteId = entry.SiteId,
                    ProjectId = entry.ProjectId,
                    DeploymentId = entry.DeploymentId,
                    // Method/Path are the request's own, fully attacker-controlled — an oversized
                    // value must never reach SaveChangesAsync: Postgres rejecting one row aborts the
                    // whole transaction, silently dropping every other entry batched alongside it
                    // (confirmed live: one overlong Method poisoned an otherwise-valid batch entirely).
                    // Clamping here, not validating, because there is nothing to reject a proxied
                    // request over — this is best-effort observability of traffic that already happened.
                    Method = Truncate(entry.Method, SiteRequestLog.MethodMaxLength),
                    Path = Truncate(entry.Path, SiteRequestLog.PathMaxLength),
                    StatusCode = entry.StatusCode,
                    DurationMs = entry.DurationMs,
                    CreatedAt = entry.CreatedAt,
                });
            }

            if (batch.Count == 0)
                continue;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
                db.SiteRequestLogs.AddRange(batch);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same "log and continue" posture as every other background worker here — a failed
                // flush drops that batch (this is best-effort observability, not a durable queue) but
                // must never take the worker down, since that would silently stop all future logging.
                logger.LogError(ex, "Failed to flush {Count} site request log(s)", batch.Count);
            }
        }
    }

    /// <summary>
    /// Postgres's <c>character varying(n)</c> counts Unicode codepoints; .NET's <see cref="string.Length"/>
    /// counts UTF-16 code units, so a cut that lands exactly inside a surrogate pair (an astral
    /// character — an emoji in a path, say) leaves a dangling unpaired surrogate. Npgsql would reject
    /// that as an encoding error, defeating the whole point of clamping here instead of validating.
    /// Backing off one further in that one case keeps every cut on a real character boundary.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        var cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
            cut--;
        return value[..cut];
    }
}
