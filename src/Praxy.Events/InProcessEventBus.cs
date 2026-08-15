using System.Collections.Concurrent;

namespace Praxy.Events;

public sealed class InProcessEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Guid, Func<PraxyEvent, ValueTask>> _handlers = new();

    public async ValueTask PublishAsync(PraxyEvent evt, CancellationToken ct = default)
    {
        foreach (var handler in _handlers.Values)
            await handler(evt);
    }

    public IDisposable Subscribe(Func<PraxyEvent, ValueTask> handler)
    {
        var key = Guid.NewGuid();
        _handlers[key] = handler;
        return new Subscription(() => _handlers.TryRemove(key, out _));
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
