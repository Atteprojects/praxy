using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
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
/// Consumes <c>praxy.webhook_deliveries</c> with <c>FOR UPDATE SKIP LOCKED</c> — the same
/// claim/execute/finalize shape as <c>SchemaJobRunner</c>. One attempt per claim: signs and POSTs
/// the event, logs a <see cref="WebhookDeliveryAttempt"/> row, and on failure either reschedules
/// with full-jitter exponential backoff or finalizes as terminally failed and registers the
/// subscription's consecutive-failure count (auto-disabling it past the configured threshold).
/// </summary>
public sealed class WebhookDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    WebhookDeliverySignal signal,
    WebhookOptions options,
    WebhookHttpClient httpClient,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Single-worker deployment assumption (true for Praxy's self-host default, same as
        // SchemaJobRunner): anything left 'delivering' after an ungraceful shutdown has no other
        // node that could be running it, so it's safe to requeue.
        await ResetStuckDeliveriesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

            WebhookDelivery? delivery;
            try
            {
                delivery = await ClaimNextAsync(db, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Webhook delivery claim failed; backing off");
                delivery = null;
            }

            if (delivery is null)
            {
                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(options.DeliveryPollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                await AttemptAsync(db, delivery, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Webhook delivery {DeliveryId} failed unexpectedly outside its own handling", delivery.Id);
            }
        }
    }

    private async Task ResetStuckDeliveriesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        var reset = await db.WebhookDeliveries
            .Where(d => d.Status == "delivering")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, "queued"), ct);
        if (reset > 0)
            logger.LogWarning("Requeued {Count} webhook delivery(ies) left 'delivering' from a previous run", reset);
    }

    private static async Task<WebhookDelivery?> ClaimNextAsync(PraxyDb db, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        const string sql = """
            UPDATE praxy.webhook_deliveries
            SET status = 'delivering'
            WHERE id = (
                SELECT id FROM praxy.webhook_deliveries
                WHERE status = 'queued' AND next_attempt_at <= now()
                ORDER BY next_attempt_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING id, subscription_id, project_id, event_id, event_type, payload, status, attempts,
                      next_attempt_at, last_attempt_at, last_status_code, last_error, redelivered_from_id, created_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new WebhookDelivery
        {
            Id = reader.GetGuid(0),
            SubscriptionId = reader.GetGuid(1),
            ProjectId = reader.GetString(2),
            EventId = reader.GetGuid(3),
            EventType = reader.GetString(4),
            Payload = reader.GetString(5),
            Status = reader.GetString(6),
            Attempts = reader.GetInt32(7),
            NextAttemptAt = reader.GetFieldValue<DateTimeOffset>(8),
            LastAttemptAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            LastStatusCode = reader.IsDBNull(10) ? null : reader.GetInt32(10),
            LastError = reader.IsDBNull(11) ? null : reader.GetString(11),
            RedeliveredFromId = reader.IsDBNull(12) ? null : reader.GetGuid(12),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(13),
        };
    }

    private async Task AttemptAsync(PraxyDb db, WebhookDelivery delivery, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.FirstOrDefaultAsync(w => w.Id == delivery.SubscriptionId, ct);
        if (subscription is null)
        {
            // A cascade delete of the subscription can race the claim; nothing left to deliver to.
            await FinalizeAsync(db, delivery, "failed", delivery.Attempts, null, "Subscription no longer exists.", ct);
            return;
        }
        if (!subscription.Enabled)
        {
            await FinalizeAsync(db, delivery, "failed", delivery.Attempts, null, "Subscription is disabled.", ct);
            return;
        }

        var attemptNumber = delivery.Attempts + 1;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = BuildBody(delivery, timestamp);
        var signature = WebhookSignature.Sign(subscription.Secret, timestamp, body);

        var sw = Stopwatch.StartNew();
        int? statusCode = null;
        string? responseBody = null;
        string? error = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(WebhookHeaders.Id, Ids.Wire(subscription.Id));
            request.Headers.Add(WebhookHeaders.Events, MatchedEventsHeader(subscription, delivery.EventType));
            request.Headers.Add(WebhookHeaders.Name, subscription.Name);
            request.Headers.Add(WebhookHeaders.ProjectId, subscription.ProjectId);
            request.Headers.Add(WebhookHeaders.Signature, signature);
            request.Headers.Add(WebhookHeaders.Timestamp, timestamp.ToString());

            using var response = await httpClient.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            statusCode = (int)response.StatusCode;
            responseBody = await ReadCappedAsync(response.Content, options.MaxResponseBodyCaptureBytes, ct);
            if (!response.IsSuccessStatusCode)
                error = $"Non-2xx response: {statusCode}";
        }
        // HttpClient surfaces both its own request timeout and real cancellation as
        // OperationCanceledException — only treat it as "our shutdown" (rethrow, let the outer
        // loop exit) when the token we passed in is the one that actually fired.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = ex.Message;
        }
        sw.Stop();

        db.WebhookDeliveryAttempts.Add(new WebhookDeliveryAttempt
        {
            Id = Ids.NewUuid(),
            DeliveryId = delivery.Id,
            AttemptNumber = attemptNumber,
            DurationMs = (int)sw.ElapsedMilliseconds,
            StatusCode = statusCode,
            ResponseBody = responseBody,
            Error = error,
        });
        await db.SaveChangesAsync(ct);

        if (error is null)
        {
            await FinalizeAsync(db, delivery, "succeeded", attemptNumber, statusCode, null, ct);
            await ResetConsecutiveFailuresAsync(db, subscription.Id, ct);
            return;
        }

        if (attemptNumber >= options.MaxAttempts)
        {
            await FinalizeAsync(db, delivery, "failed", attemptNumber, statusCode, error, ct);
            await RegisterFailureAsync(db, subscription, ct);
            return;
        }

        var delay = WebhookBackoff.Compute(
            attemptNumber, TimeSpan.FromSeconds(options.BackoffBaseSeconds), TimeSpan.FromSeconds(options.BackoffCapSeconds));
        await db.WebhookDeliveries.Where(d => d.Id == delivery.Id).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, "queued")
            .SetProperty(d => d.Attempts, attemptNumber)
            .SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow + delay)
            .SetProperty(d => d.LastAttemptAt, DateTimeOffset.UtcNow)
            .SetProperty(d => d.LastStatusCode, statusCode)
            .SetProperty(d => d.LastError, error), ct);
        signal.Notify();
    }

    private static Task FinalizeAsync(
        PraxyDb db, WebhookDelivery delivery, string status, int attempts, int? statusCode, string? error,
        CancellationToken ct) =>
        db.WebhookDeliveries.Where(d => d.Id == delivery.Id).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, status)
            .SetProperty(d => d.Attempts, attempts)
            .SetProperty(d => d.LastAttemptAt, DateTimeOffset.UtcNow)
            .SetProperty(d => d.LastStatusCode, statusCode)
            .SetProperty(d => d.LastError, error), ct);

    private async Task RegisterFailureAsync(PraxyDb db, WebhookSubscription subscription, CancellationToken ct)
    {
        var failures = subscription.ConsecutiveFailures + 1;
        if (failures >= options.DisableAfterConsecutiveFailures)
        {
            await db.WebhookSubscriptions.Where(w => w.Id == subscription.Id).ExecuteUpdateAsync(s => s
                .SetProperty(w => w.ConsecutiveFailures, failures)
                .SetProperty(w => w.Enabled, false)
                .SetProperty(w => w.DisabledReason, $"Disabled automatically after {failures} consecutive failed deliveries.")
                .SetProperty(w => w.UpdatedAt, DateTimeOffset.UtcNow), ct);
            logger.LogWarning(
                "Webhook subscription {SubscriptionId} auto-disabled after {Failures} consecutive failures",
                subscription.Id, failures);
        }
        else
        {
            await db.WebhookSubscriptions.Where(w => w.Id == subscription.Id).ExecuteUpdateAsync(s => s
                .SetProperty(w => w.ConsecutiveFailures, failures)
                .SetProperty(w => w.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }
    }

    private static Task ResetConsecutiveFailuresAsync(PraxyDb db, Guid subscriptionId, CancellationToken ct) =>
        db.WebhookSubscriptions.Where(w => w.Id == subscriptionId && w.ConsecutiveFailures != 0)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.ConsecutiveFailures, 0), ct);

    private static string MatchedEventsHeader(WebhookSubscription subscription, string eventType)
    {
        var expanded = ChannelGrammar.ExpandEventNames(eventType);
        var matched = subscription.Events.Where(expanded.Contains).ToArray();
        return string.Join(",", matched.Length > 0 ? matched : [eventType]);
    }

    private static string BuildBody(WebhookDelivery delivery, long timestampUnixSeconds)
    {
        var data = JsonNode.Parse(delivery.Payload) ?? new JsonObject();
        var envelope = new JsonObject
        {
            ["event"] = delivery.EventType,
            ["timestamp"] = DateTimeOffset.FromUnixTimeSeconds(timestampUnixSeconds).ToString("O"),
            ["projectId"] = delivery.ProjectId,
            ["data"] = data,
        };
        return envelope.ToJsonString();
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes];
        var total = 0;
        int read;
        while (total < maxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), ct)) > 0)
            total += read;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
