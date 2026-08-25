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

namespace Praxy.Sites;

/// <summary>
/// Consumes <c>praxy.site_deployments</c> with <c>FOR UPDATE SKIP LOCKED</c> — the same claim/execute
/// shape as <c>Praxy.Functions.FunctionBuildWorker</c>: build the uploaded tar (or, for a git-sourced
/// deployment — Sites Phase 4 — a fresh clone of the pushed commit) into an image, stream the log into
/// a queryable row as it happens, and on success activate the deployment (which, unlike Functions,
/// actually starts a long-lived container — see <c>SitesService.ActivateAsync</c>) — but only when the
/// build came from an upload, or from a git push to the site's configured production branch. A
/// git-sourced push to any other branch builds and goes <c>ready</c> without activating, staying
/// reachable at its own Phase 2 preview URL exactly like any other non-active deployment already is.
/// </summary>
public sealed class SiteBuildWorker(
    IServiceScopeFactory scopeFactory, SiteBuildSignal signal, SitesOptions options, SiteDockerExecutor docker,
    IGitRepositoryCloner cloner, ILogger<SiteBuildWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStuckBuildsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

            SiteDeployment? deployment;
            try
            {
                deployment = await ClaimNextAsync(db, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Site build claim failed; backing off");
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
                logger.LogError(ex, "Site deployment {DeploymentId} build failed unexpectedly outside its own handling", deployment.Id);
            }
        }
    }

    private async Task ResetStuckBuildsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        var reset = await db.SiteDeployments
            .Where(d => d.Status == "building")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, "queued"), ct);
        if (reset > 0)
            logger.LogWarning("Requeued {Count} site build(s) left 'building' from a previous run", reset);
    }

    private static async Task<SiteDeployment?> ClaimNextAsync(PraxyDb db, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        const string sql = """
            UPDATE praxy.site_deployments
            SET status = 'building', updated_at = now()
            WHERE id = (
                SELECT id FROM praxy.site_deployments
                WHERE status = 'queued'
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING id, site_id, project_id, source_size_bytes, status, source, commit_sha,
                      commit_message, branch, build_log, error, image_tag, container_id, created_at,
                      updated_at, activated_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new SiteDeployment
        {
            Id = reader.GetGuid(0),
            SiteId = reader.GetGuid(1),
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
            ContainerId = reader.IsDBNull(12) ? null : reader.GetString(12),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(13),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(14),
            ActivatedAt = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
        };
    }

    private async Task BuildAsync(SiteDeployment deployment, CancellationToken ct)
    {
        using var loadScope = scopeFactory.CreateScope();
        var loadDb = loadScope.ServiceProvider.GetRequiredService<PraxyDb>();
        var loadSites = loadScope.ServiceProvider.GetRequiredService<SitesService>();
        var site = await loadDb.Sites.FirstOrDefaultAsync(s => s.Id == deployment.SiteId, ct);
        var isGit = deployment.Source == "git";

        // An upload-sourced deployment needs its stored tar bytes; a git-sourced one instead needs
        // the site to still have a connected repository and the deployment to carry the commit it was
        // queued for — either missing means there's nothing left to build from.
        var source = isGit ? null : await loadDb.SiteDeploymentSources.FirstOrDefaultAsync(s => s.DeploymentId == deployment.Id, ct);
        var missingSource = isGit
            ? site?.RepositoryFullName is null || deployment.CommitSha is null
            : source is null;
        if (site is null || missingSource)
        {
            await FinalizeFailedAsync(deployment.Id, "",
                isGit ? "Site, its connected repository, or the pushed commit no longer exists."
                      : "Site or uploaded source no longer exists.", ct);
            return;
        }

        var logBuffer = new StringBuilder();
        var logLock = new object();
        using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flushTask = FlushLoopAsync(deployment.Id, logBuffer, logLock, flushCts.Token);

        var envVars = await loadSites.DecryptedEnvVarsAsync(site.Id, ct);
        var imageTag = $"praxy-site-{Ids.Wire(deployment.Id)}:latest";

        SiteDockerExecutor.BuildResult result;
        try
        {
            if (isGit)
            {
                var githubApp = loadScope.ServiceProvider.GetRequiredService<GitHubAppService>();
                var token = await githubApp.GetInstallationTokenForRepositoryAsync(site.RepositoryFullName!, ct);
                await using var checkout = await cloner.CloneAsync(site.RepositoryFullName!, deployment.CommitSha!, token, ct);
                await using var context = await SiteRuntimeTemplates.BuildContextFromDirectoryAsync(
                    site.RootDirectory, options.NodeBaseImage, envVars.Keys, checkout.Path, ct);
                result = await docker.BuildImageAsync(context, imageTag, envVars, line =>
                {
                    lock (logLock) logBuffer.Append(line);
                }, ct);
            }
            else
            {
                await using var userTar = new MemoryStream(source!.Tar);
                await using var context = await SiteRuntimeTemplates.BuildContextAsync(
                    site.RootDirectory, options.NodeBaseImage, envVars.Keys, userTar, ct);
                result = await docker.BuildImageAsync(context, imageTag, envVars, line =>
                {
                    lock (logLock) logBuffer.Append(line);
                }, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string snapshot;
            lock (logLock) snapshot = logBuffer.ToString();
            result = new SiteDockerExecutor.BuildResult(false, snapshot, ex.Message);
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
        await finalizeDb.SiteDeploymentSources.Where(s => s.DeploymentId == deployment.Id).ExecuteDeleteAsync(ct);

        if (!result.Success)
        {
            await finalizeDb.SiteDeployments.Where(d => d.Id == deployment.Id).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, "failed")
                .SetProperty(d => d.BuildLog, finalLog)
                .SetProperty(d => d.Error, result.Error)
                .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await finalizeDb.SiteDeployments.Where(d => d.Id == deployment.Id).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, "ready")
            .SetProperty(d => d.BuildLog, finalLog)
            .SetProperty(d => d.ImageTag, imageTag)
            .SetProperty(d => d.UpdatedAt, now), ct);

        // Auto-activate every successful upload-sourced build, mirroring Functions' own default — the
        // console's explicit "Activate" action on any ready deployment still exists for rollback. A
        // git-sourced build only auto-activates when it came from the site's configured production
        // branch (Sites Phase 4); a push to any other branch builds and stops here, staying reachable
        // at its own Phase 2 preview URL without ever touching the site's active deployment.
        // ActivateAsync starts the real container, so this is where a site actually goes live.
        if (isGit && deployment.Branch != site.ProductionBranch)
            return;

        using var activateScope = scopeFactory.CreateScope();
        var activateDb = activateScope.ServiceProvider.GetRequiredService<PraxyDb>();
        var activateSites = activateScope.ServiceProvider.GetRequiredService<SitesService>();
        var freshSite = await activateDb.Sites.FirstAsync(s => s.Id == site.Id, ct);
        var freshDeployment = await activateDb.SiteDeployments.FirstAsync(d => d.Id == deployment.Id, ct);
        try
        {
            await activateSites.ActivateAsync(freshSite, freshDeployment, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Site deployment {DeploymentId} built successfully but failed to start its container", deployment.Id);
            await activateDb.SiteDeployments.Where(d => d.Id == deployment.Id).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, "failed")
                .SetProperty(d => d.Error, $"Build succeeded but the container failed to start: {ex.Message}")
                .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }
    }

    private async Task FinalizeFailedAsync(Guid deploymentId, string log, string error, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        await db.SiteDeploymentSources.Where(s => s.DeploymentId == deploymentId).ExecuteDeleteAsync(ct);
        await db.SiteDeployments.Where(d => d.Id == deploymentId).ExecuteUpdateAsync(s => s
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
                await db.SiteDeployments.Where(d => d.Id == deploymentId)
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
