namespace Praxy.Events;

/// <summary>
/// In-process pub/sub for best-effort consumers (realtime, caches).
/// Durable consumers read the <c>praxy.events</c> outbox instead.
/// </summary>
public interface IEventBus
{
    ValueTask PublishAsync(PraxyEvent evt, CancellationToken ct = default);
    IDisposable Subscribe(Func<PraxyEvent, ValueTask> handler);
}
