namespace Praxy.Functions;

/// <summary>Wakes <see cref="FunctionExecutionWorker"/> immediately after an async execution is queued (event trigger, cron, or a manual async invoke) — same shape as <c>Praxy.Webhooks.WebhookDeliverySignal</c>; the worker still polls on a timer as a fallback.</summary>
public sealed class FunctionExecutionSignal
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
