using Microsoft.EntityFrameworkCore;
using Praxy.Persistence;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// Deletes rows past their configured age from the tables that otherwise grow forever:
/// <c>praxy.events</c>, <c>praxy.webhook_deliveries</c> (cascades to <c>webhook_delivery_attempts</c>
/// at the FK level — see <c>PraxyDb</c>'s <see cref="Persistence.Entities.WebhookDeliveryAttempt"/>
/// mapping), <c>praxy.audit_log</c>, and <c>praxy.site_requests</c>. Same shape as
/// <c>FunctionPoolSweeper</c>: a loop, a try/catch around the actual work that logs and continues
/// rather than crashing the host, then <see cref="Task.Delay(TimeSpan, CancellationToken)"/> on a
/// configurable interval.
/// </summary>
public sealed class RetentionSweeper(
    IServiceScopeFactory scopeFactory,
    RetentionOptions options,
    ILogger<RetentionSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.SweepIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        var now = DateTimeOffset.UtcNow;

        // Only a row both dispatchers have claimed is safe to drop — an unclaimed row past the
        // window is left for the next sweep rather than force-deleted (e.g. a stuck function build,
        // or a webhook subscription mid-backoff that hasn't been picked up yet).
        var eventsCutoff = now.AddDays(-options.EventsMaxAgeDays);
        var eventsDeleted = await db.Events
            .Where(e => e.CreatedAt < eventsCutoff
                && e.WebhooksDispatchedAt != null
                && e.FunctionsDispatchedAt != null)
            .ExecuteDeleteAsync(ct);

        // Never a queued/delivering delivery — only a terminal one is safe to drop.
        var deliveriesCutoff = now.AddDays(-options.WebhookDeliveriesMaxAgeDays);
        var deliveriesDeleted = await db.WebhookDeliveries
            .Where(d => d.CreatedAt < deliveriesCutoff
                && (d.Status == "succeeded" || d.Status == "failed"))
            .ExecuteDeleteAsync(ct);

        var auditCutoff = now.AddDays(-options.AuditLogMaxAgeDays);
        var auditDeleted = await db.AuditLog
            .Where(a => a.CreatedAt < auditCutoff)
            .ExecuteDeleteAsync(ct);

        // Every row here is already a terminal, complete record the instant it's written (unlike
        // events/deliveries, there's no in-flight status to protect) — a plain age filter is enough.
        var siteRequestsCutoff = now.AddDays(-options.SiteRequestsMaxAgeDays);
        var siteRequestsDeleted = await db.SiteRequestLogs
            .Where(r => r.CreatedAt < siteRequestsCutoff)
            .ExecuteDeleteAsync(ct);

        if (eventsDeleted > 0 || deliveriesDeleted > 0 || auditDeleted > 0 || siteRequestsDeleted > 0)
        {
            logger.LogInformation(
                "Retention sweep deleted {Events} events, {Deliveries} webhook deliveries, {Audit} audit log entries, {SiteRequests} site request logs",
                eventsDeleted, deliveriesDeleted, auditDeleted, siteRequestsDeleted);
        }
    }
}
