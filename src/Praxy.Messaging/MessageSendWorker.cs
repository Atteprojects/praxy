using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Messaging;

/// <summary>
/// Consumes <c>praxy.message_targets</c> with <c>FOR UPDATE SKIP LOCKED</c> — the same claim shape
/// <c>WebhookDeliveryWorker</c>/<c>FunctionExecutionWorker</c> use, applied here to a message's fan-
/// out to its resolved targets rather than to the outbox (sending is operator-triggered, never an
/// event reaction, so there's nothing to claim from <c>praxy.events</c>). One attempt per target —
/// no retry/backoff: unlike webhook deliveries, nothing in the roadmap calls for it, and a failed
/// send is already visible per-target rather than silently swallowed.
/// </summary>
public sealed class MessageSendWorker(
    IServiceScopeFactory scopeFactory,
    MessageSendSignal signal,
    MessagingOptions options,
    ILogger<MessageSendWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStuckAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

            MessageTarget? target;
            try
            {
                target = await ClaimNextAsync(db, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Message target claim failed; backing off");
                target = null;
            }

            if (target is null)
            {
                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(options.SendPollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                await SendAsync(scope.ServiceProvider, db, target, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Message target {TargetId} failed unexpectedly outside its own handling", target.Id);
            }
        }
    }

    private async Task ResetStuckAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        var reset = await db.MessageTargets
            .Where(t => t.Status == "sending")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "queued"), ct);
        if (reset > 0)
            logger.LogWarning("Requeued {Count} message target(s) left 'sending' from a previous run", reset);
    }

    private static async Task<MessageTarget?> ClaimNextAsync(PraxyDb db, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        const string sql = """
            UPDATE praxy.message_targets
            SET status = 'sending'
            WHERE id = (
                SELECT id FROM praxy.message_targets
                WHERE status = 'queued'
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING id, message_id, project_id, target_id, identifier, status, error, delivered_at, created_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new MessageTarget
        {
            Id = reader.GetGuid(0),
            MessageId = reader.GetGuid(1),
            ProjectId = reader.GetString(2),
            TargetId = reader.GetGuid(3),
            Identifier = reader.GetString(4),
            Status = reader.GetString(5),
            Error = reader.IsDBNull(6) ? null : reader.GetString(6),
            DeliveredAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
        };
    }

    private static async Task SendAsync(IServiceProvider services, PraxyDb db, MessageTarget target, CancellationToken ct)
    {
        var message = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == target.MessageId, ct);
        if (message is null)
        {
            // A cascade delete of the message can race the claim; nothing left to send.
            await FinalizeTargetAsync(db, target.Id, "failed", "Message no longer exists.", ct);
            await MaybeCompleteAsync(db, target.MessageId, ct);
            return;
        }

        var resolver = services.GetRequiredService<EmailProviderResolver>();
        try
        {
            var sender = await resolver.ResolveAsync(target.ProjectId, ct);
            await sender.SendAsync(new Auth.EmailMessage(target.Identifier, message.Subject, message.Body), ct);
            await FinalizeTargetAsync(db, target.Id, "sent", null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await FinalizeTargetAsync(db, target.Id, "failed", ex.Message, ct);
        }
        await MaybeCompleteAsync(db, target.MessageId, ct);
    }

    private static Task FinalizeTargetAsync(PraxyDb db, Guid targetId, string status, string? error, CancellationToken ct) =>
        db.MessageTargets.Where(t => t.Id == targetId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, status)
            .SetProperty(t => t.Error, error is { Length: > 2048 } e ? e[..2048] : error)
            .SetProperty(t => t.DeliveredAt, status == "sent" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null), ct);

    /// <summary>Flips the parent message to 'completed' once every target it fanned out to has reached a terminal state.</summary>
    private static async Task MaybeCompleteAsync(PraxyDb db, Guid messageId, CancellationToken ct)
    {
        var stillPending = await db.MessageTargets.AnyAsync(
            t => t.MessageId == messageId && (t.Status == "queued" || t.Status == "sending"), ct);
        if (stillPending)
            return;
        await db.Messages.Where(m => m.Id == messageId && m.Status == "processing").ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, "completed")
            .SetProperty(m => m.CompletedAt, DateTimeOffset.UtcNow), ct);
    }
}
