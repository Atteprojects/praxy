namespace Praxy.Sites;

/// <summary>Wakes <see cref="SiteScreenshotWorker"/> immediately after a deployment activates — same shape as <see cref="SiteBuildSignal"/>; the worker still polls on a timer as a fallback.</summary>
public sealed class SiteScreenshotSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled and not yet consumed — the pending wakeup covers this too.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}
