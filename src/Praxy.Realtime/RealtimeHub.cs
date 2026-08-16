using Microsoft.Extensions.Hosting;
using Praxy.Core;
using Praxy.Events;

namespace Praxy.Realtime;

/// <summary>
/// The one consumer of <see cref="IEventBus"/> for realtime (architecture.md §6/§7): fans out
/// row/team/account events by permission-filtered channel match, closes sockets on session
/// revocation, and flags connections for lazy role re-resolution on user/team/membership changes
/// rather than re-resolving eagerly. Runs for the app's whole lifetime via <see cref="IHostedService"/>
/// — nothing else needs to inject it, so it never needs to be resolved directly.
/// </summary>
public sealed class RealtimeHub(IEventBus bus, ConnectionRegistry registry) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = bus.Subscribe(OnEventAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private ValueTask OnEventAsync(PraxyEvent evt)
    {
        if (TryParseSessionDelete(evt.Type, out var sessionId))
        {
            registry.CloseSession(evt.ProjectId, sessionId, "Session revoked.");
            return ValueTask.CompletedTask;
        }

        if (IsRoleChangingEvent(evt.Type))
            registry.MarkForRevalidation(evt.ProjectId, evt.Permissions);

        var channels = ChannelGrammar.ChannelsForEvent(evt.Type);
        if (channels.Count == 0)
            return ValueTask.CompletedTask;

        var matches = registry.Match(evt, channels);
        if (matches.Count == 0)
            return ValueTask.CompletedTask;

        var events = ChannelGrammar.ExpandEventNames(evt.Type);
        foreach (var (connection, subscriptions) in matches)
            connection.Enqueue(ServerMessage.Event(events, channels, subscriptions, evt.At, evt.Payload));

        return ValueTask.CompletedTask;
    }

    /// <summary>Matches <c>users.&lt;uid&gt;.sessions.&lt;sid&gt;.delete</c>, published by <c>AppAuthService</c> on revocation.</summary>
    private static bool TryParseSessionDelete(string type, out Guid sessionId)
    {
        var parts = type.Split('.');
        if (parts is ["users", _, "sessions", var sid, "delete"] && Ids.TryParseWire(sid, out sessionId))
            return true;
        sessionId = default;
        return false;
    }

    /// <summary>
    /// <c>users.*.update*</c>/<c>.delete</c> (labels, status, email verification) and any
    /// <c>teams.*</c>/membership event can change a caller's resolved role set. Session
    /// create/delete and row events never do, so they skip the (rare-path) revalidation scan.
    /// </summary>
    private static bool IsRoleChangingEvent(string type)
    {
        var parts = type.Split('.');
        if (parts.Length >= 3 && parts[0] == "users" && parts[2] is "update" or "delete")
            return true;
        return parts.Length >= 2 && parts[0] == "teams";
    }
}
