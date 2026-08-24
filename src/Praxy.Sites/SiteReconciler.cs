using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Praxy.Persistence;

namespace Praxy.Sites;

/// <summary>
/// Runs an immediate reconciliation pass on <c>api</c> startup, then repeats on
/// <see cref="SitesOptions.ReconcileIntervalSeconds"/>: for every enabled site with an active
/// <c>ready</c> deployment, ensure a container is actually running, starting one if not. Handles
/// both "api crashed mid-activation" (praxy-sites.md's stated reason for this service existing —
/// Functions has no equivalent because its containers are acquired lazily per-invocation) and "api
/// restarted cleanly but the in-memory <see cref="SiteContainerRegistry"/> is now empty" — the
/// common case on every single restart, since the registry holds nothing durable across process
/// boundaries by design.
/// </summary>
public sealed class SiteReconciler(
    IServiceScopeFactory scopeFactory, SiteDockerExecutor docker, SiteContainerRegistry registry,
    SitesOptions options, ILogger<SiteReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Site reconciliation pass failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.ReconcileIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();
        var sites = scope.ServiceProvider.GetRequiredService<SitesService>();

        var candidates = await db.Sites.Where(s => s.Enabled && s.ActiveDeploymentId != null).ToListAsync(ct);
        foreach (var site in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var activeId = site.ActiveDeploymentId!.Value;

            if (registry.TryGet(activeId, out var tracked))
            {
                if (await docker.IsRunningAsync(tracked.ContainerId, ct))
                    continue;
                registry.Remove(activeId);
            }

            var deployment = await db.SiteDeployments.FirstOrDefaultAsync(d => d.Id == activeId, ct);
            if (deployment is null || deployment.Status != "ready" || deployment.ImageTag is null)
                continue;

            // A container may already be running under the deployment's recorded id — api restarted
            // cleanly (the container itself, with RestartPolicy: unless-stopped, never went down) and
            // only the in-memory registry is empty. Re-inspect and repopulate rather than starting a
            // duplicate container for the same deployment.
            if (deployment.ContainerId is { } containerId && await docker.IsRunningAsync(containerId, ct))
            {
                try
                {
                    registry.Set(activeId, await docker.InspectAddressAsync(containerId, ct));
                    continue;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Site {SiteId}'s recorded container {ContainerId} is running but its address could not be read — starting a fresh one", site.Id, containerId);
                }
            }

            logger.LogWarning("Site {SiteId} has no running container for its active deployment — starting one (reconciliation)", site.Id);
            try
            {
                await sites.ActivateAsync(site, deployment, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reconciliation failed to start a container for site {SiteId}", site.Id);
            }
        }
    }
}
