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

namespace Praxy.Functions;

/// <summary>
/// The outbox consumer for event-triggered functions — same claim shape as
/// <c>Praxy.Webhooks.WebhookOutboxDispatcher</c>, reusing <see cref="ChannelGrammar.ExpandEventNames"/>
/// verbatim for pattern matching, but claiming independently via <c>OutboxEvent.FunctionsDispatchedAt</c>
/// rather than the webhook dispatcher's <c>WebhooksDispatchedAt</c> — two consumers of one outbox
/// row need two claim markers, or whichever claims first hides the row from the other (see the
/// remarks on <c>OutboxEvent.WebhooksDispatchedAt</c>). Unlike webhooks, there is no retry/backoff
/// fan-out table: a matching function gets exactly one <see cref="FunctionExecution"/> row directly —
/// event triggers are best-effort, same as realtime.
/// </summary>
/// <remarks>
/// Scope boundary carried over from Phase 6 (see docs/handoff/phase-6-report.md): <c>praxy.events</c>
/// is written only by row writes (<c>RowsService.WriteOutboxAsync</c>). A function subscribed to a
/// non-row event pattern (e.g. <c>users.*.create</c>) will never fire — that event never reaches the
/// outbox in the first place. The console's trigger picker only offers row-event presets for the
/// same reason webhooks' event picker does.
/// </remarks>
public sealed class FunctionEventDispatcher(
    IServiceScopeFactory scopeFactory,
    FunctionExecutionSignal signal,
    FunctionsOptions options,
    ILogger<FunctionEventDispatcher> logger) : BackgroundService
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
                logger.LogError(ex, "Function event dispatch failed; backing off");
                dispatched = false;
            }

            if (!dispatched)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.ExecutionPollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

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
            WHERE functions_dispatched_at IS NULL
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

        var functions = await db.Functions
            .Where(f => f.ProjectId == claimed.ProjectId && f.Enabled)
            .ToListAsync(ct);

        var triggered = 0;
        if (functions.Count > 0)
        {
            var expanded = ChannelGrammar.ExpandEventNames(claimed.Type).ToHashSet();
            foreach (var fn in functions)
            {
                if (fn.Events.Length == 0 || !fn.Events.Any(expanded.Contains))
                    continue;
                triggered++;
                db.FunctionExecutions.Add(new FunctionExecution
                {
                    Id = Ids.NewUuid(),
                    FunctionId = fn.Id,
                    ProjectId = claimed.ProjectId,
                    Trigger = "event",
                    Async = true,
                    Method = "POST",
                    Path = "/",
                    RequestBody = claimed.Payload,
                    TriggeredBy = $"event:{claimed.Type}",
                });
            }
        }

        await using (var markCmd = new NpgsqlCommand(
            "UPDATE praxy.events SET functions_dispatched_at = now() WHERE id = @id", conn, dbTx))
        {
            markCmd.Parameters.AddWithValue("id", claimed.Id);
            await markCmd.ExecuteNonQueryAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (triggered > 0)
            signal.Notify();
        return true;
    }
}
