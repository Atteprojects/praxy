using System.Collections.Concurrent;
using Praxy.Core;
using Praxy.Events;
using Praxy.Persistence.Entities;

namespace Praxy.Auth;

public sealed record CachedSession(Session Session, User User);

/// <summary>Keeps the hot auth path off the database. Entries live ~60s.</summary>
public interface ISessionCache
{
    bool TryGet(Guid sessionId, out CachedSession entry);
    void Set(CachedSession entry);
    void Invalidate(Guid sessionId);
}

/// <summary>
/// In-memory TTL cache invalidated through the event bus: <c>sessions.delete</c> events remove
/// the session immediately (revocation is instant despite the TTL), and user update/delete
/// events drop every session of that user so status/label changes take effect.
/// </summary>
public sealed class InMemorySessionCache : ISessionCache, IDisposable
{
    private readonly ConcurrentDictionary<Guid, (CachedSession Entry, DateTimeOffset ExpiresAt)> _entries = new();
    private readonly TimeSpan _ttl;
    private readonly IDisposable _subscription;

    public InMemorySessionCache(IEventBus bus, TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromSeconds(60);
        _subscription = bus.Subscribe(OnEvent);
    }

    public bool TryGet(Guid sessionId, out CachedSession entry)
    {
        entry = null!;
        if (!_entries.TryGetValue(sessionId, out var cached))
            return false;
        if (cached.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(sessionId, out _);
            return false;
        }
        entry = cached.Entry;
        return true;
    }

    public void Set(CachedSession entry)
    {
        // Opportunistic sweep so a burst of short-lived sessions cannot grow the map unboundedly.
        if (_entries.Count > 10_000)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (key, value) in _entries)
                if (value.ExpiresAt <= now)
                    _entries.TryRemove(key, out _);
        }
        _entries[entry.Session.Id] = (entry, DateTimeOffset.UtcNow + _ttl);
    }

    public void Invalidate(Guid sessionId) => _entries.TryRemove(sessionId, out _);

    private ValueTask OnEvent(PraxyEvent evt)
    {
        // users.<uid>.sessions.<sid>.delete | users.<uid>.update[.attr] | users.<uid>.delete
        var parts = evt.Type.Split('.');
        if (parts is ["users", _, "sessions", var sid, "delete"] && Ids.TryParseWire(sid, out var sessionId))
        {
            Invalidate(sessionId);
        }
        else if (parts.Length >= 3 && parts[0] == "users" && parts[2] is "update" or "delete" &&
                 Ids.TryParseWire(parts[1], out var userId))
        {
            foreach (var (key, value) in _entries)
                if (value.Entry.User.Id == userId)
                    _entries.TryRemove(key, out _);
        }
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _subscription.Dispose();
}
