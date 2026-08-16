using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Realtime;

namespace Praxy.Webhooks;

/// <summary>
/// The outbox consumer: claims un-dispatched <c>praxy.events</c> rows with <c>FOR UPDATE SKIP
/// LOCKED</c> (mirrors <c>SchemaJobRunner</c>'s claim shape) and expands each into one
/// <see cref="WebhookDelivery"/> row per enabled subscription whose pattern matches — reusing
/// <see cref="ChannelGrammar.ExpandEventNames"/>, the same wildcard expansion realtime's fan-out
/// already computes, rather than a second matcher. The claim, the delivery-row inserts and the
/// dispatched-at stamp share one transaction, so a crash mid-dispatch leaves the event exactly as
/// "not yet dispatched" — at-least-once, never a partial fan-out.
/// </summary>
public sealed class WebhookOutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    WebhookDeliverySignal deliverySignal,
    WebhookOptions options,
    ILogger<WebhookOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool dispatched;
            try
            {
                dispatched = await DispatchNextAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Webhook outbox dispatch failed; backing off");
                dispatched = false;
            }

            if (!dispatched)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.DispatchPollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <returns>False when there was nothing to dispatch (the caller should back off before polling again).</returns>
    private async Task<bool> DispatchNextAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var dbTx = (NpgsqlTransaction)tx.GetDbTransaction();

        const string claimSql = """
            SELECT id, project_id, type, payload, created_at
            FROM praxy.events
            WHERE dispatched_at IS NULL
            ORDER BY created_at
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """;

        OutboxEvent? claimed = null;
        await using (var cmd = new NpgsqlCommand(claimSql, conn, dbTx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                claimed = new OutboxEvent
                {
                    Id = reader.GetGuid(0),
                    ProjectId = reader.GetString(1),
                    Type = reader.GetString(2),
                    Payload = reader.GetString(3),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(4),
                };
            }
        }

        if (claimed is null)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        var subscriptions = await db.WebhookSubscriptions
            .Where(w => w.ProjectId == claimed.ProjectId && w.Enabled)
            .ToListAsync(ct);

        var matched = 0;
        if (subscriptions.Count > 0)
        {
            var expanded = ChannelGrammar.ExpandEventNames(claimed.Type).ToHashSet();
            var now = DateTimeOffset.UtcNow;
            foreach (var subscription in subscriptions)
            {
                if (!subscription.Events.Any(expanded.Contains))
                    continue;
                matched++;
                db.WebhookDeliveries.Add(new WebhookDelivery
                {
                    Id = Ids.NewUuid(),
                    SubscriptionId = subscription.Id,
                    ProjectId = claimed.ProjectId,
                    EventId = claimed.Id,
                    EventType = claimed.Type,
                    Payload = claimed.Payload,
                    NextAttemptAt = now,
                });
            }
        }

        await using (var markCmd = new NpgsqlCommand(
            "UPDATE praxy.events SET dispatched_at = now() WHERE id = @id", conn, dbTx))
        {
            markCmd.Parameters.AddWithValue("id", claimed.Id);
            await markCmd.ExecuteNonQueryAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (matched > 0)
            deliverySignal.Notify();
        return true;
    }
}
