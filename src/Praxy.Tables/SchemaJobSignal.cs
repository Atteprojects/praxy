namespace Praxy.Tables;

/// <summary>
/// Wakes the <c>SchemaJobRunner</c> immediately when a job is enqueued, instead of waiting for
/// its next poll tick. Purely an in-process responsiveness optimization — the runner still polls
/// on a timer as a fallback, so a missed signal (e.g. a second replica) is never fatal.
/// </summary>
public sealed class SchemaJobSignal
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
