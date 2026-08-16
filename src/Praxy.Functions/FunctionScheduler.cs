using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Functions;

/// <summary>
/// Cron-driven invocation. Claims the single most-overdue scheduled, enabled function via
/// <c>FOR UPDATE SKIP LOCKED</c> (same shape as every other claim loop in this project), computes
/// its next occurrence with Cronos (dotnet-stack.md: verified current — 0.13.0, actively maintained
/// by the Hangfire team, UTC-safe), and stamps <c>NextScheduledRunAt</c> forward in the same
/// transaction as the claim so two instances can never double-fire the same tick.
/// </summary>
/// <remarks>
/// <c>FunctionsService</c> computes and stores the first <c>NextScheduledRunAt</c> at
/// create/update time — this loop's <c>next_scheduled_run_at IS NULL</c> clause is a safety net for
/// rows that predate that (or a cron string that failed to parse and was reset to null), not the
/// common path. Without that, a freshly-scheduled function would read as "overdue" and fire once
/// immediately, which is not what "run daily at midnight" means to whoever configured it.
/// </remarks>
public sealed class FunctionScheduler(
    IServiceScopeFactory scopeFactory,
    FunctionExecutionSignal signal,
    FunctionsOptions options,
    ILogger<FunctionScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool fired;
            try
            {
                fired = await FireDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Function schedule check failed; backing off");
                fired = false;
            }

            if (!fired)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.SchedulePollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<bool> FireDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var dbTx = (NpgsqlTransaction)tx.GetDbTransaction();

        const string claimSql = """
            SELECT id, project_id, schedule
            FROM praxy.functions
            WHERE enabled = true AND schedule IS NOT NULL
              AND (next_scheduled_run_at IS NULL OR next_scheduled_run_at <= now())
            ORDER BY COALESCE(next_scheduled_run_at, created_at)
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """;

        Guid id = default;
        string projectId = "";
        string? schedule = null;
        var found = false;
        await using (var cmd = new NpgsqlCommand(claimSql, conn, dbTx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                found = true;
                id = reader.GetGuid(0);
                projectId = reader.GetString(1);
                schedule = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        if (!found)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        DateTimeOffset? next = null;
        if (schedule is not null)
        {
            try
            {
                next = CronExpression.Parse(schedule).GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
            }
            catch (CronFormatException ex)
            {
                // Already validated at save time (FunctionsService) — a parse failure here means
                // the expression can't be scheduled at all. Leave next null (never fires again by
                // this loop) rather than tight-looping on a row it can never satisfy.
                logger.LogWarning(ex, "Function {FunctionId}'s cron expression '{Schedule}' failed to parse; disabling its schedule", id, schedule);
            }
        }

        await using (var updateCmd = new NpgsqlCommand(
            "UPDATE praxy.functions SET next_scheduled_run_at = @next WHERE id = @id", conn, dbTx))
        {
            updateCmd.Parameters.AddWithValue("id", id);
            updateCmd.Parameters.AddWithValue("next", (object?)next ?? DBNull.Value);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        db.FunctionExecutions.Add(new FunctionExecution
        {
            Id = Ids.NewUuid(),
            FunctionId = id,
            ProjectId = projectId,
            Trigger = "schedule",
            Async = true,
            Method = "GET",
            Path = "/",
            TriggeredBy = "schedule",
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        signal.Notify();
        return true;
    }
}
