using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Praxy.Persistence;

namespace Praxy.Sites;

/// <summary>
/// Consumes <c>praxy.site_deployments</c> with the same <c>FOR UPDATE SKIP LOCKED</c> claim shape as
/// <see cref="SiteBuildWorker"/>, but simpler: capturing a screenshot has no durable "in progress"
/// state to get stuck in the way a build's <c>status='building'</c> can, so there is no equivalent of
/// <c>ResetStuckBuildsAsync</c> — a claim consumed by a worker that then crashes just costs one of
/// <see cref="SitesOptions.ScreenshotMaxAttempts"/>, reclaimed naturally on the next poll. Kept off
/// the activation critical path entirely (<c>SitesService.ActivateAsync</c> only notifies the signal)
/// so a slow or failed capture can never delay a site going live.
/// </summary>
public sealed class SiteScreenshotWorker(
    IServiceScopeFactory scopeFactory, SiteScreenshotSignal signal, SiteContainerRegistry registry,
    SiteDockerExecutor docker, SitesOptions options, ILogger<SiteScreenshotWorker> logger) : BackgroundService
{
    private sealed record Claimed(Guid Id, Guid SiteId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

            Claimed? claimed;
            try
            {
                claimed = await ClaimNextAsync(db, options.ScreenshotMaxAttempts, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Site screenshot claim failed; backing off");
                claimed = null;
            }

            if (claimed is null)
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
                await CaptureAsync(claimed, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Site deployment {DeploymentId} screenshot capture failed unexpectedly outside its own handling", claimed.Id);
            }
        }
    }

    private static async Task<Claimed?> ClaimNextAsync(PraxyDb db, int maxAttempts, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        const string sql = """
            UPDATE praxy.site_deployments
            SET screenshot_attempts = screenshot_attempts + 1
            WHERE id = (
                SELECT id FROM praxy.site_deployments
                WHERE activated_at IS NOT NULL
                  AND screenshot_captured_at IS NULL
                  AND screenshot_attempts < @maxAttempts
                ORDER BY activated_at DESC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING id, site_id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("maxAttempts", maxAttempts);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new Claimed(reader.GetGuid(0), reader.GetGuid(1));
    }

    private async Task CaptureAsync(Claimed claimed, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

        var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == claimed.SiteId, ct);
        if (site is null || site.ActiveDeploymentId != claimed.Id)
        {
            // Superseded by a newer deployment (or the site was deleted) before this claim ran —
            // nobody will ever see this deployment's screenshot, so give up on it now rather than
            // spend a real capture attempt on content nobody's looking at.
            await db.SiteDeployments.Where(d => d.Id == claimed.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ScreenshotCapturedAt, DateTimeOffset.UtcNow), ct);
            return;
        }

        if (!registry.TryGet(site.Id, out var running))
        {
            // Not up yet (still starting, or briefly down) — leave ScreenshotCapturedAt null so the
            // claim query picks it up again, bounded by ScreenshotMaxAttempts.
            return;
        }

        var png = await docker.CaptureScreenshotAsync(running, ct);
        if (png is null)
        {
            // A real attempt was made and failed (image pull, container start, timeout — see
            // SiteDockerExecutor's own logging for which). Back off before the next poll can
            // reclaim this same row, rather than burning through ScreenshotMaxAttempts in the
            // space of a few seconds — found live: three attempts exhausted in well under a
            // minute during a fresh deploy's startup load, with no room for a transient failure
            // (a slow image pull, a momentary daemon hiccup) to clear before giving up for good.
            await Task.Delay(TimeSpan.FromSeconds(options.ScreenshotRetryDelaySeconds), ct);
            return;
        }

        db.SiteDeploymentScreenshots.Add(new Persistence.Entities.SiteDeploymentScreenshot { DeploymentId = claimed.Id, Png = png });
        var now = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await db.SiteDeployments.Where(d => d.Id == claimed.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ScreenshotCapturedAt, now), ct);
        // Bumps Site.UpdatedAt so the console's cache-busted <img> (keyed off it) picks up the new
        // image on its next poll instead of serving a stale browser-cached miss indefinitely.
        await db.Sites.Where(s => s.Id == site.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, now), ct);
    }
}
