namespace Praxy.Webhooks;

/// <summary>
/// Wakes the <see cref="WebhookDeliveryWorker"/> immediately when a delivery becomes claimable —
/// the outbox dispatcher notifies after creating rows, and a console "Redeliver" click notifies
/// too — instead of waiting for the next poll tick. Purely a responsiveness optimization, same
/// shape as <c>Praxy.Tables.SchemaJobSignal</c>: the worker still polls on a timer as a fallback.
/// </summary>
public sealed class WebhookDeliverySignal
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
