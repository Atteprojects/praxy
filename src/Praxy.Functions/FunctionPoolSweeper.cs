using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Praxy.Functions;

/// <summary>
/// Periodically evicts warm-pool containers idle past <see cref="FunctionsOptions.MaxIdleSeconds"/>,
/// and reclaims crash-orphaned containers once at startup — the same shape as
/// <c>FunctionExecutionWorker.ResetStuckAsync</c>, which requeues rows left mid-flight by a previous
/// process before entering its own loop.
/// </summary>
public sealed class FunctionPoolSweeper(
    WarmPool pool, DockerExecutor docker, FunctionsOptions options, ILogger<FunctionPoolSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // security-review-phase-1 follow-up: a hard crash (SIGKILL/OOM/host reboot) leaves function
        // containers running with nothing tracking them — the warm pool only stops what it holds in
        // memory, which a crash discards. Loud rather than silent: a non-zero count here means the
        // previous process died badly, which is worth seeing in the log.
        try
        {
            var reclaimed = await docker.RemoveOrphanedContainersAsync(stoppingToken);
            if (reclaimed > 0)
                logger.LogWarning("Reclaimed {Count} orphaned function container(s) left by a previous run", reclaimed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Orphaned function container sweep failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pool.SweepIdleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Function warm-pool sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PoolSweepIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
