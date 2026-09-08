using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Praxy.Persistence;

namespace Praxy.Sites;

/// <summary>
/// Stops preview containers <see cref="SiteContainerRegistry"/> hasn't seen a proxied request for in
/// <see cref="SitesOptions.PreviewIdleSeconds"/> — the Sites analogue of
/// <c>Praxy.Functions.FunctionPoolSweeper</c> sweeping <c>WarmPool</c>, applied here only to
/// deployments that are <em>not</em> any site's <see cref="Persistence.Entities.Site.ActiveDeploymentId"/>.
/// The active deployment's container is deliberately exempt regardless of how idle it looks by proxied
/// traffic — it's meant to run continuously (<see cref="SiteReconciler"/> owns keeping it up), never
/// idle-swept like a preview. Separate from <see cref="SiteReconciler"/> on purpose: that service's job
/// is "keep the required containers up," this one's is "tear down the optional ones" — different
/// direction, kept as different services rather than one doing both.
/// </summary>
public sealed class SitePreviewSweeper(
    IServiceScopeFactory scopeFactory, SiteContainerRegistry registry, SiteDockerExecutor docker,
    SitesOptions options, ILogger<SitePreviewSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Once at startup, before the idle loop: reclaim containers no deployment row references.
        // Same shape as FunctionPoolSweeper's own startup reclaim, and loud for the same reason — a
        // non-zero count means a previous process abandoned them, which is worth seeing rather than
        // silently tidying away. The idle loop below can never reach these: it only knows containers
        // this process put in the in-memory registry, which a restart empties.
        try
        {
            var reclaimed = await ReclaimUnreferencedAsync(stoppingToken);
            if (reclaimed > 0)
                logger.LogWarning("Reclaimed {Count} unreferenced site container(s) left by a previous run", reclaimed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unreferenced site container sweep failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Site preview sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PreviewSweepIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<int> ReclaimUnreferencedAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

        // Read the database first, then let the executor list Docker. The other order would widen
        // the window in which SiteReconciler commits a container id this pass has already decided
        // it never saw; the age guard below is what actually closes it, but reading first keeps the
        // window as small as the two services' startup allows.
        var referenced = (await db.SiteDeployments
                .Where(d => d.ContainerId != null)
                .Select(d => d.ContainerId!)
                .ToListAsync(ct))
            .ToHashSet();

        return await docker.RemoveUnreferencedContainersAsync(
            referenced, System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), ct);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(options.PreviewIdleSeconds);
        var idleCandidates = registry.IdleSince(cutoff);
        if (idleCandidates.Count == 0)
            return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

        // Never sweep a deployment that is currently any site's active one, however idle it looks by
        // request traffic alone — the always-on production model still applies to it.
        var activeIds = await db.Sites
            .Where(s => s.ActiveDeploymentId != null)
            .Select(s => s.ActiveDeploymentId!.Value)
            .ToListAsync(ct);
        var activeSet = activeIds.ToHashSet();

        foreach (var deploymentId in idleCandidates)
        {
            ct.ThrowIfCancellationRequested();
            if (activeSet.Contains(deploymentId))
                continue;

            // Re-checked atomically at removal time — a request may have touched this deployment
            // (bumping its last-used timestamp) in the moment between IdleSince's snapshot and here.
            if (!registry.TryRemoveIfIdle(deploymentId, cutoff, out var container))
                continue;

            logger.LogInformation("Stopping idle preview container for deployment {DeploymentId}", deploymentId);
            await docker.StopAndRemoveAsync(container.ContainerId, CancellationToken.None);
        }
    }
}
