using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
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
    InstanceKey key, AccountJwtService jwts)
{
    /// <summary>
    /// <paramref name="ct"/> is the caller's own token (HTTP <c>RequestAborted</c> for the sync
    /// endpoint) — it can legitimately become cancelled mid-invocation for reasons that have
    /// nothing to do with whether the invocation itself succeeded or failed (client navigated away,
    /// a proxy hop timed out, network hiccup). Every write this method makes uses
    /// <see cref="CancellationToken.None"/> instead, deliberately: a caller disconnecting must never
    /// leave the execution row stuck in "waiting" forever — the row is the only record of what
    /// happened, sync caller or not, and it must always reach a final status.
    /// </summary>
    public async Task RunAsync(FunctionExecution execution, CancellationToken ct)
    {
        // security-review-phase-3: every current FunctionExecution creator stamps ProjectId from the
        // very same already-project-scoped fn/claimed row it takes FunctionId from (FunctionsService,
        // FunctionEventDispatcher, FunctionScheduler), so these can never actually disagree today —
        // but nothing enforced that invariant here, and this is the lookup that feeds both the minted
        // JWT's project claim and PRAXY_PROJECT_ID below. Checking it explicitly means a future
        // creation path (or a hand-inserted row) can't silently mint credentials for the wrong project.
        var fn = await db.Functions.FirstOrDefaultAsync(f => f.Id == execution.FunctionId && f.ProjectId == execution.ProjectId, ct);
        if (fn is null || !fn.Enabled || fn.ActiveDeploymentId is not { } deploymentId)
        {
            await FinalizeAsync(execution.Id, null, "failed", 0, "", "", null, false,
                fn is null ? "Function no longer exists." : !fn.Enabled ? "Function is disabled." : "No active deployment.",
                CancellationToken.None);
            return;
        }

        var deployment = await db.FunctionDeployments.FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (deployment is null || deployment.Status != "ready" || deployment.ImageTag is null)
        {
            await FinalizeAsync(execution.Id, deploymentId, "failed", 0, "", "", null, false,
                "The function's active deployment is not ready.", CancellationToken.None);
            return;
        }

        var timeoutSeconds = execution.Async ? fn.TimeoutSeconds : Math.Min(fn.TimeoutSeconds, options.MaxSyncTimeoutSeconds);
        var env = await BuildEnvAsync(fn, execution, timeoutSeconds, ct);
        var wasWarm = pool.IsWarm(deploymentId);

        // security-review-phase-1: a container carrying an invocation-scoped credential
        // (PRAXY_FUNCTION_JWT/USER_ID for a specific app user, or PRAXY_FUNCTION_API_KEY for a
        // scoped schedule/event trigger — see BuildEnvAsync) must never be handed to a later
        // invocation that didn't ask for that credential. WarmPool bakes env into the container at
        // start and never updates it, so pooling one of these would silently leak the first
        // invocation's identity to every later invocation of the same deployment that happens to
        // land on the still-warm container. poolable: false always cold-starts and is never added
        // to the pool; this method stops it itself once the invocation is done.
        var identityScoped = env.ContainsKey("PRAXY_FUNCTION_JWT") || env.ContainsKey("PRAXY_FUNCTION_API_KEY");

        // security-review-phase-1 follow-up: reserve capacity BEFORE the try. A non-poolable
        // container is outside WarmPoolSize's accounting entirely, so without this cap concurrent
        // user-triggered invocations spawn one container each, bounded by nothing (the functions
        // rate limiter is a per-caller fixed window — it bounds arrival rate, not concurrency).
        // Deliberately outside the try: the broad catch below turns every *execution* failure into
        // a recorded row and a 200 with status "failed", which is the wrong shape for "the server
        // never started this" — that has to reach the caller as a retryable 503. The row is still
        // finalized first, so this method keeps its "must always reach a final status" invariant.
        var reservedSlot = false;
        if (identityScoped)
        {
            reservedSlot = await pool.TryReserveIsolatedSlotAsync(ct);
            if (!reservedSlot)
            {
                await FinalizeAsync(execution.Id, deploymentId, "failed", 0, "", "", 0, false,
                    "No isolated container capacity available.", CancellationToken.None);
                throw new PraxyException(
                    503, ErrorTypes.FunctionCapacityExceeded,
                    "The server is at capacity for isolated function containers. Retry shortly.",
                    retryAfterSeconds: options.IsolatedContainerWaitSeconds);
            }
        }

        var sw = Stopwatch.StartNew();
        RunningContainer? container = null;
        try
        {
            container = await pool.AcquireAsync(deploymentId, deployment.ImageTag, env, ct, poolable: !identityScoped);
            var result = await docker.InvokeAsync(
                container, execution.Method, execution.Path, execution.RequestBody ?? "",
                new Dictionary<string, string>(), TimeSpan.FromSeconds(timeoutSeconds), ct);
            sw.Stop();

            await FinalizeAsync(
                execution.Id, deploymentId, result.Errors is null ? "completed" : "failed", result.StatusCode,
                Cap(result.Body), Cap(result.Logs), (int)sw.ElapsedMilliseconds, !wasWarm, result.Errors,
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            await FinalizeAsync(execution.Id, deploymentId, "failed", 0, "", "", (int)sw.ElapsedMilliseconds, !wasWarm,
                $"Execution timed out after {timeoutSeconds}s.", CancellationToken.None);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await FinalizeAsync(execution.Id, deploymentId, "failed", 0, "", "", (int)sw.ElapsedMilliseconds, !wasWarm,
                ex.Message, CancellationToken.None);
        }
        finally
        {
            if (identityScoped && container is not null)
                await docker.StopAndRemoveAsync(container.ContainerId, CancellationToken.None);
            // Released only after the container is actually gone — releasing earlier would let the
            // next invocation start one while this one is still holding its memory/CPU.
            if (reservedSlot)
                pool.ReleaseIsolatedSlot();
        }
    }

    /// <summary>
    /// security-review-phase-3: a few seconds beyond <paramref name="timeoutSeconds"/> for the actual
    /// callback HTTP round trip (container start, DNS/connect, the JWT-verification call itself),
    /// not a second invocation window — the container carrying this JWT is stopped the moment this
    /// invocation ends (see identityScoped's remarks in <see cref="RunAsync"/>), so nothing can use
    /// the extra time to run new work.
    /// </summary>
    private const int JwtGraceSeconds = 10;

    private async Task<Dictionary<string, string>> BuildEnvAsync(
        FunctionDef fn, FunctionExecution execution, int timeoutSeconds, CancellationToken ct)
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
        //
        // security-review-phase-1's own follow-up question, answered here: now that this JWT is
        // guaranteed freshly minted per invocation (never sitting in a warm container's env), its
        // lifetime is sized to the invocation it was minted for — timeoutSeconds (the sync-endpoint's
        // 30s cap, or the function's own up-to-900s async ceiling) plus JwtGraceSeconds — rather than
        // the flat 15-minute AccountJwtService.DefaultLifetime shared with the self-service
        // /account/jwts endpoint. A sync invocation used to get a token valid up to 30x longer than
        // the request that could ever use it; a function exfiltrating its own JWT (a compromised
        // dependency logging it, or phoning it home) now only ever holds a bearer credential for
        // roughly as long as the invocation that leaked it could possibly still be running.
        var triggeredBy = execution.TriggeredBy;
        if (triggeredBy is not null && triggeredBy.StartsWith("user:", StringComparison.Ordinal) &&
            Ids.TryParseWire(triggeredBy["user:".Length..], out var userId))
        {
            env["PRAXY_FUNCTION_JWT"] = jwts.Mint(execution.ProjectId, userId, TimeSpan.FromSeconds(timeoutSeconds + JwtGraceSeconds));
            env["PRAXY_FUNCTION_USER_ID"] = triggeredBy["user:".Length..];
        }
        // The gap this fills (see docs/handoff/functions-scheduled-credentials-report.md): a
        // schedule- or event-triggered execution has no calling user to inherit a JWT from above, so
        // it gets nothing at all unless an operator explicitly granted this function platform
        // scopes. Deliberately as narrow as the two trigger shapes that actually lack a caller
        // identity — "console" and "key:<id>" triggers already have one (the operator's own session,
        // or the key itself) and stay exactly as they are today.
        else if ((triggeredBy == "schedule" || (triggeredBy?.StartsWith("event:", StringComparison.Ordinal) ?? false)) &&
            fn.PlatformScopes.Length > 0 && fn.PlatformApiKeySecretProtected is { } protectedSecret &&
            key.Decrypt(protectedSecret) is { } secret)
        {
            env["PRAXY_FUNCTION_API_KEY"] = secret;
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
