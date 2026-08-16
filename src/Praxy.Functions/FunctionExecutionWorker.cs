using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Functions;

/// <summary>
/// Consumes <c>praxy.function_executions</c> rows with <c>Async = true</c> and
/// <c>Status = 'waiting'</c> via <c>FOR UPDATE SKIP LOCKED</c> — the roadmap's "async executions
/// store their output" requirement lands as: a row is created immediately (queryable from the
/// moment it's requested), this worker claims and runs it, and the result is written back rather
/// than discarded. Sync executions never reach this worker — the invoking request runs
/// <see cref="FunctionExecutionService"/> directly and finalizes its own row, so the claim query's
/// <c>async = true</c> filter is what keeps the two paths from racing each other on the same row.
/// </summary>
public sealed class FunctionExecutionWorker(
    IServiceScopeFactory scopeFactory,
    FunctionExecutionSignal signal,
    FunctionsOptions options,
    ILogger<FunctionExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStuckAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

            FunctionExecution? execution;
            try
            {
                execution = await ClaimNextAsync(db, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Function execution claim failed; backing off");
                execution = null;
            }

            if (execution is null)
            {
                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(options.ExecutionPollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                var runner = scope.ServiceProvider.GetRequiredService<FunctionExecutionService>();
                await runner.RunAsync(execution, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Function execution {ExecutionId} failed unexpectedly outside its own handling", execution.Id);
            }
        }
    }

    private async Task ResetStuckAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        var reset = await db.FunctionExecutions
            .Where(e => e.Status == "processing")
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, "waiting"), ct);
        if (reset > 0)
            logger.LogWarning("Requeued {Count} function execution(s) left 'processing' from a previous run", reset);
    }

    private static async Task<FunctionExecution?> ClaimNextAsync(PraxyDb db, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        const string sql = """
            UPDATE praxy.function_executions
            SET status = 'processing'
            WHERE id = (
                SELECT id FROM praxy.function_executions
                WHERE status = 'waiting' AND async = true
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING id, function_id, project_id, deployment_id, trigger, async, status, method, path,
                      request_body, status_code, response_body, logs, errors, duration_ms, cold_start,
                      triggered_by, created_at, completed_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new FunctionExecution
        {
            Id = reader.GetGuid(0),
            FunctionId = reader.GetGuid(1),
            ProjectId = reader.GetString(2),
            DeploymentId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
            Trigger = reader.GetString(4),
            Async = reader.GetBoolean(5),
            Status = reader.GetString(6),
            Method = reader.GetString(7),
            Path = reader.GetString(8),
            RequestBody = reader.IsDBNull(9) ? null : reader.GetString(9),
            StatusCode = reader.IsDBNull(10) ? null : reader.GetInt32(10),
            ResponseBody = reader.IsDBNull(11) ? null : reader.GetString(11),
            Logs = reader.GetString(12),
            Errors = reader.IsDBNull(13) ? null : reader.GetString(13),
            DurationMs = reader.IsDBNull(14) ? null : reader.GetInt32(14),
            ColdStart = reader.GetBoolean(15),
            TriggeredBy = reader.IsDBNull(16) ? null : reader.GetString(16),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(17),
            CompletedAt = reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
        };
    }
}
