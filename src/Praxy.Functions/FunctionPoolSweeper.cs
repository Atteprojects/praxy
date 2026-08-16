using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Praxy.Functions;

/// <summary>Periodically evicts warm-pool containers idle past <see cref="FunctionsOptions.MaxIdleSeconds"/>.</summary>
public sealed class FunctionPoolSweeper(WarmPool pool, FunctionsOptions options, ILogger<FunctionPoolSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
