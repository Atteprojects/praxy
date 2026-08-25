using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Vcs;

namespace Praxy.Functions;

/// <summary>
/// Consumes <c>praxy.function_deployments</c> with <c>FOR UPDATE SKIP LOCKED</c> — the same
/// claim/execute shape as <c>SchemaJobRunner</c> and <c>WebhookDeliveryWorker</c>: build the
/// uploaded tar (or, for a git-sourced deployment — Functions git integration — a fresh clone of the
/// pushed commit) into an image, stream the log into a queryable row as it happens, and on success
/// auto-activate the deployment — but only when the build came from an upload, or from a git push to
/// the function's configured production branch. A git-sourced push to any other branch builds and
/// goes <c>ready</c> without activating, staying reachable only via the console's explicit "Activate"
/// action, exactly the state an unactivated upload could already be in.
/// </summary>
public sealed class FunctionBuildWorker(
    IServiceScopeFactory scopeFactory,
    FunctionBuildSignal signal,
    FunctionsOptions options,
    DockerExecutor docker,
    WarmPool pool,
    IGitRepositoryCloner cloner,
    ILogger<FunctionBuildWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStuckBuildsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

            FunctionDeployment? deployment;
            try
            {
                deployment = await ClaimNextAsync(db, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Function build claim failed; backing off");
                deployment = null;
            }

            if (deployment is null)
            {
                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(options.BuildPollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                await BuildAsync(deployment, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Function deployment {DeploymentId} build failed unexpectedly outside its own handling", deployment.Id);
            }
        }
    }

    private async Task ResetStuckBuildsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        var reset = await db.FunctionDeployments
            .Where(d => d.Status == "building")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, "queued"), ct);
        if (reset > 0)
            logger.LogWarning("Requeued {Count} function build(s) left 'building' from a previous run", reset);
    }

    private static async Task<FunctionDeployment?> ClaimNextAsync(PraxyDb db, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        const string sql = """
            UPDATE praxy.function_deployments
            SET status = 'building', updated_at = now()
            WHERE id = (
                SELECT id FROM praxy.function_deployments
                WHERE status = 'queued'
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING id, function_id, project_id, source_size_bytes, status, source, commit_sha,
                      commit_message, branch, build_log, error, image_tag, created_at, updated_at,
                      activated_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new FunctionDeployment
        {
            Id = reader.GetGuid(0),
            FunctionId = reader.GetGuid(1),
            ProjectId = reader.GetString(2),
            SourceSizeBytes = reader.GetInt64(3),
            Status = reader.GetString(4),
            Source = reader.GetString(5),
            CommitSha = reader.IsDBNull(6) ? null : reader.GetString(6),
            CommitMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
            Branch = reader.IsDBNull(8) ? null : reader.GetString(8),
            BuildLog = reader.GetString(9),
            Error = reader.IsDBNull(10) ? null : reader.GetString(10),
            ImageTag = reader.IsDBNull(11) ? null : reader.GetString(11),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(12),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(13),
            ActivatedAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
        };
    }

    private async Task BuildAsync(FunctionDeployment deployment, CancellationToken ct)
    {
        using var loadScope = scopeFactory.CreateScope();
        var loadDb = loadScope.ServiceProvider.GetRequiredService<PraxyDb>();
        var fn = await loadDb.Functions.FirstOrDefaultAsync(f => f.Id == deployment.FunctionId, ct);
        var isGit = deployment.Source == "git";

        // An upload-sourced deployment needs its stored tar bytes; a git-sourced one instead needs
        // the function to still have a connected repository and the deployment to carry the commit it
        // was queued for — either missing means there's nothing left to build from.
        var source = isGit ? null : await loadDb.FunctionDeploymentSources.FirstOrDefaultAsync(s => s.DeploymentId == deployment.Id, ct);
        var missingSource = isGit
            ? fn?.RepositoryFullName is null || deployment.CommitSha is null
            : source is null;
        if (fn is null || missingSource)
        {
            await FinalizeFailedAsync(deployment.Id, "",
                isGit ? "Function, its connected repository, or the pushed commit no longer exists."
                      : "Function or uploaded source no longer exists.", ct);
            return;
        }

        var logBuffer = new StringBuilder();
        var logLock = new object();
        using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flushTask = FlushLoopAsync(deployment.Id, logBuffer, logLock, flushCts.Token);

        var baseImage = fn.Runtime == FunctionRuntimes.Dart ? options.DartBaseImage : options.NodeBaseImage;
        var imageTag = $"praxy-fn-{Ids.Wire(deployment.Id)}:latest";

        DockerExecutor.BuildResult result;
        try
        {
            if (isGit)
            {
                var githubApp = loadScope.ServiceProvider.GetRequiredService<GitHubAppService>();
                var token = await githubApp.GetInstallationTokenForRepositoryAsync(fn.RepositoryFullName!, ct);
                await using var checkout = await cloner.CloneAsync(fn.RepositoryFullName!, deployment.CommitSha!, token, ct);
                await using var context = await RuntimeTemplates.BuildContextFromDirectoryAsync(
                    fn.Runtime, fn.Entrypoint, baseImage, checkout.Path, ct);
                result = await docker.BuildImageAsync(context, imageTag, line =>
                {
                    lock (logLock) logBuffer.Append(line);
                }, ct);
            }
            else
            {
                await using var userTar = new MemoryStream(source!.Tar);
                await using var context = await RuntimeTemplates.BuildContextAsync(fn.Runtime, fn.Entrypoint, baseImage, userTar, ct);
                result = await docker.BuildImageAsync(context, imageTag, line =>
                {
                    lock (logLock) logBuffer.Append(line);
                }, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string snapshot;
            lock (logLock) snapshot = logBuffer.ToString();
            result = new DockerExecutor.BuildResult(false, snapshot, ex.Message);
        }
        finally
        {
            await flushCts.CancelAsync();
            try
            {
                await flushTask;
            }
            catch (OperationCanceledException)
            {
                // Expected — the flush loop's own delay observes the cancellation.
            }
        }

        string finalLog;
        lock (logLock)
            finalLog = logBuffer.ToString();

        using var finalizeScope = scopeFactory.CreateScope();
        var finalizeDb = finalizeScope.ServiceProvider.GetRequiredService<PraxyDb>();
        // The build context no longer needs the uploaded bytes regardless of outcome.
        await finalizeDb.FunctionDeploymentSources.Where(s => s.DeploymentId == deployment.Id).ExecuteDeleteAsync(ct);

        if (!result.Success)
        {
            await finalizeDb.FunctionDeployments.Where(d => d.Id == deployment.Id).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, "failed")
                .SetProperty(d => d.BuildLog, finalLog)
                .SetProperty(d => d.Error, result.Error)
                .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await finalizeDb.FunctionDeployments.Where(d => d.Id == deployment.Id).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, "ready")
            .SetProperty(d => d.BuildLog, finalLog)
            .SetProperty(d => d.ImageTag, imageTag)
            .SetProperty(d => d.UpdatedAt, now), ct);

        // Auto-activate every successful upload-sourced build — the console's explicit "Activate"
        // action on any ready deployment still exists for rollback; this default is what the
        // owner-test's "deploy from console → invoke sync" expects without an extra click. A
        // git-sourced build only auto-activates when it came from the function's configured
        // production branch (Functions git integration); a push to any other branch builds and stops
        // here, staying `ready` but unactivated until the console's explicit Activate action.
        if (isGit && deployment.Branch != fn.ProductionBranch)
            return;

        var previousActiveId = fn.ActiveDeploymentId;
        await finalizeDb.FunctionDeployments.Where(d => d.Id == deployment.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ActivatedAt, now), ct);
        await finalizeDb.Functions.Where(f => f.Id == fn.Id).ExecuteUpdateAsync(s => s
            .SetProperty(f => f.ActiveDeploymentId, deployment.Id)
            .SetProperty(f => f.UpdatedAt, now), ct);

        if (previousActiveId is { } prev && prev != deployment.Id)
            await pool.EvictAsync(prev, CancellationToken.None);
    }

    private async Task FinalizeFailedAsync(Guid deploymentId, string log, string error, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        await db.FunctionDeploymentSources.Where(s => s.DeploymentId == deploymentId).ExecuteDeleteAsync(ct);
        await db.FunctionDeployments.Where(d => d.Id == deploymentId).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, "failed")
            .SetProperty(d => d.BuildLog, log)
            .SetProperty(d => d.Error, error)
            .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>Persists the in-progress build log roughly once a second so the console's poll always sees recent output, without a DB write per streamed line.</summary>
    private async Task FlushLoopAsync(Guid deploymentId, StringBuilder buffer, object bufferLock, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                string snapshot;
                lock (bufferLock)
                    snapshot = buffer.ToString();

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
                await db.FunctionDeployments.Where(d => d.Id == deploymentId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.BuildLog, snapshot)
                        .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown of the flush loop once the build itself finishes.
        }
    }
}
