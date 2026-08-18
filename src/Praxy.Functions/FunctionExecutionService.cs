using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Functions;

/// <summary>
/// The one place that actually invokes a function — shared by the sync HTTP endpoint (awaited
/// directly, 30s hard cap) and <see cref="FunctionExecutionWorker"/> (async executions). Resolves
/// the active deployment, decrypts env vars (<see cref="InstanceKey"/> — the same AES-256-GCM
/// mechanism already used for OAuth provider tokens, reused rather than standing up a second
/// project-key layer), mints a scoped user JWT when the caller invoked as a specific app user, and
/// always leaves a queryable <see cref="FunctionExecution"/> row behind — sync or async, the
/// console can always see what happened.
/// </summary>
public sealed class FunctionExecutionService(
    PraxyDb db, DockerExecutor docker, WarmPool pool, FunctionsOptions options,
    InstanceKey key, AccountJwtService jwts, ILogger<FunctionExecutionService> logger)
{
    public async Task RunAsync(FunctionExecution execution, CancellationToken ct)
    {
        logger.LogWarning("DIAG: RunAsync entered for execution {ExecutionId}, ct.IsCancellationRequested={Cancelled}", execution.Id, ct.IsCancellationRequested);
        var fn = await db.Functions.FirstOrDefaultAsync(f => f.Id == execution.FunctionId, ct);
        if (fn is null || !fn.Enabled || fn.ActiveDeploymentId is not { } deploymentId)
        {
            await FinalizeAsync(execution.Id, null, "failed", 0, "", "", null, false,
                fn is null ? "Function no longer exists." : !fn.Enabled ? "Function is disabled." : "No active deployment.", ct);
            return;
        }

        var deployment = await db.FunctionDeployments.FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (deployment is null || deployment.Status != "ready" || deployment.ImageTag is null)
        {
            await FinalizeAsync(execution.Id, deploymentId, "failed", 0, "", "", null, false,
                "The function's active deployment is not ready.", ct);
            return;
        }

        logger.LogWarning("DIAG: about to BuildEnvAsync for {ExecutionId}", execution.Id);
        var env = await BuildEnvAsync(fn, execution, ct);
        logger.LogWarning("DIAG: BuildEnvAsync done for {ExecutionId}", execution.Id);
        var timeoutSeconds = execution.Async ? fn.TimeoutSeconds : Math.Min(fn.TimeoutSeconds, options.MaxSyncTimeoutSeconds);
        logger.LogWarning("DIAG: about to call pool.IsWarm for {ExecutionId}", execution.Id);
        var wasWarm = pool.IsWarm(deploymentId);
        logger.LogWarning("DIAG: pool.IsWarm returned {WasWarm} for {ExecutionId}", wasWarm, execution.Id);

        var sw = Stopwatch.StartNew();
        try
        {
            logger.LogWarning("DIAG: about to call pool.AcquireAsync for {ExecutionId}", execution.Id);
            var container = await pool.AcquireAsync(deploymentId, deployment.ImageTag, env, ct);
            logger.LogWarning("DIAG: pool.AcquireAsync returned for {ExecutionId}", execution.Id);
            var result = await docker.InvokeAsync(
                container, execution.Method, execution.Path, execution.RequestBody ?? "",
                new Dictionary<string, string>(), TimeSpan.FromSeconds(timeoutSeconds), ct);
            sw.Stop();

            await FinalizeAsync(
                execution.Id, deploymentId, result.Errors is null ? "completed" : "failed", result.StatusCode,
                Cap(result.Body), Cap(result.Logs), (int)sw.ElapsedMilliseconds, !wasWarm, result.Errors, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            await FinalizeAsync(execution.Id, deploymentId, "failed", 0, "", "", (int)sw.ElapsedMilliseconds, !wasWarm,
                $"Execution timed out after {timeoutSeconds}s.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            await FinalizeAsync(execution.Id, deploymentId, "failed", 0, "", "", (int)sw.ElapsedMilliseconds, !wasWarm,
                ex.Message, ct);
        }
    }

    private async Task<Dictionary<string, string>> BuildEnvAsync(FunctionDef fn, FunctionExecution execution, CancellationToken ct)
    {
        var stored = await db.FunctionEnvVars.Where(v => v.FunctionId == fn.Id).ToListAsync(ct);
        var env = new Dictionary<string, string>();
        foreach (var v in stored)
        {
            if (key.Decrypt(v.ProtectedValue) is { } plain)
                env[v.Key] = plain;
        }

        // "Scoped user JWT injected into invocations that need to act as a specific user" —
        // research/appwrite-api.md's Phase-7-required JWT flow. Only present when the caller that
        // triggered this execution was a specific app user, never for event/schedule/console triggers.
        if (execution.TriggeredBy is { } triggeredBy &&
            triggeredBy.StartsWith("user:", StringComparison.Ordinal) &&
            Ids.TryParseWire(triggeredBy["user:".Length..], out var userId))
        {
            env["PRAXY_FUNCTION_JWT"] = jwts.Mint(execution.ProjectId, userId, AccountJwtService.DefaultLifetime);
            env["PRAXY_FUNCTION_USER_ID"] = triggeredBy["user:".Length..];
        }

        env["PRAXY_FUNCTION_ID"] = Ids.Wire(fn.Id);
        env["PRAXY_PROJECT_ID"] = execution.ProjectId;
        return env;
    }

    private string Cap(string value) =>
        value.Length > options.MaxResponseCaptureBytes ? value[..options.MaxResponseCaptureBytes] : value;

    private Task FinalizeAsync(
        Guid executionId, Guid? deploymentId, string status, int statusCode, string body, string logs,
        int? durationMs, bool coldStart, string? errors, CancellationToken ct) =>
        db.FunctionExecutions.Where(e => e.Id == executionId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.DeploymentId, deploymentId)
            .SetProperty(e => e.Status, status)
            .SetProperty(e => e.StatusCode, statusCode)
            .SetProperty(e => e.ResponseBody, body)
            .SetProperty(e => e.Logs, logs)
            .SetProperty(e => e.DurationMs, durationMs)
            .SetProperty(e => e.ColdStart, coldStart)
            .SetProperty(e => e.Errors, errors)
            .SetProperty(e => e.CompletedAt, DateTimeOffset.UtcNow), ct);
}
