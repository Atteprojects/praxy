using System.Collections.Concurrent;
using Praxy.Events;

namespace Praxy.Realtime;

internal readonly record struct IndexKey(string ProjectId, string Role, string Channel);
internal readonly record struct SubKey(Guid ConnectionId, string SubscriptionId);

/// <summary>
/// project→role→channel→connection index (roadmap.md Phase 4) for O(1) fan-out lookups against an
/// event's precomputed <see cref="PraxyEvent.Permissions"/>, plus a channel-only index for
/// <see cref="Connection.Bypass"/> connections that see every event on a subscribed channel
/// regardless of role. All cross-connection structures here are concurrent; a single connection's
/// own index entries are only ever written from that connection's own read-loop task (see
/// <see cref="Connection"/>'s doc comment), so no per-connection locking is needed.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, Connection> _byId = new();
    private readonly ConcurrentDictionary<IndexKey, ConcurrentDictionary<SubKey, Connection>> _roleIndex = new();
    private readonly ConcurrentDictionary<(string ProjectId, string Channel), ConcurrentDictionary<SubKey, Connection>> _bypassIndex = new();
    private readonly ConcurrentDictionary<string, int> _projectCounts = new();

    public int CountForProject(string projectId) => _projectCounts.GetValueOrDefault(projectId);

    /// <summary>Registers the connection if the project is under quota; otherwise leaves it unregistered so the caller closes it with <see cref="RealtimeCloseCodes.Overloaded"/>.</summary>
    public bool TryRegister(Connection connection, int maxConnectionsPerProject)
    {
        var count = _projectCounts.AddOrUpdate(connection.ProjectId, 1, (_, n) => n + 1);
        if (count > maxConnectionsPerProject)
        {
            _projectCounts.AddOrUpdate(connection.ProjectId, 0, (_, n) => Math.Max(0, n - 1));
            return false;
        }
        _byId[connection.Id] = connection;
        return true;
    }

    public void Unregister(Connection connection)
    {
        if (!_byId.TryRemove(connection.Id, out _))
            return;
        _projectCounts.AddOrUpdate(connection.ProjectId, 0, (_, n) => Math.Max(0, n - 1));

        foreach (var (role, channel, subscriptionId) in connection.IndexEntries)
            RemoveRoleEntry(connection, role, channel, subscriptionId);
        connection.IndexEntries.Clear();

        foreach (var (channel, subscriptionId) in connection.BypassIndexEntries)
            RemoveBypassEntry(connection, channel, subscriptionId);
        connection.BypassIndexEntries.Clear();
    }

    /// <summary>
    /// Applies the connection's current <see cref="Connection.Subscriptions"/> × <see cref="Connection.Roles"/>
    /// to the index by diffing against what was there before — call after any subscribe,
    /// unsubscribe, or role re-resolution. Owner-thread-only (the connection's own read loop).
    /// </summary>
    public void Reindex(Connection connection)
    {
        if (connection.Bypass)
        {
            var desired = new HashSet<(string Channel, string SubscriptionId)>();
            foreach (var (subscriptionId, channels) in connection.Subscriptions)
                foreach (var channel in channels)
                    desired.Add((channel, subscriptionId));

            foreach (var entry in connection.BypassIndexEntries)
                if (!desired.Contains(entry))
                    RemoveBypassEntry(connection, entry.Channel, entry.SubscriptionId);
            foreach (var entry in desired)
                if (!connection.BypassIndexEntries.Contains(entry))
                    _bypassIndex.GetOrAdd((connection.ProjectId, entry.Channel), static _ => new())[new SubKey(connection.Id, entry.SubscriptionId)] = connection;

            connection.BypassIndexEntries.Clear();
            connection.BypassIndexEntries.UnionWith(desired);
            return;
        }

        var desiredRoles = new HashSet<(string Role, string Channel, string SubscriptionId)>();
        foreach (var role in connection.Roles)
            foreach (var (subscriptionId, channels) in connection.Subscriptions)
                foreach (var channel in channels)
                    desiredRoles.Add((role, channel, subscriptionId));

        foreach (var entry in connection.IndexEntries)
            if (!desiredRoles.Contains(entry))
                RemoveRoleEntry(connection, entry.Role, entry.Channel, entry.SubscriptionId);
        foreach (var entry in desiredRoles)
            if (!connection.IndexEntries.Contains(entry))
                _roleIndex.GetOrAdd(new IndexKey(connection.ProjectId, entry.Role, entry.Channel), static _ => new())[new SubKey(connection.Id, entry.SubscriptionId)] = connection;

        connection.IndexEntries.Clear();
        connection.IndexEntries.UnionWith(desiredRoles);
    }

    private void RemoveRoleEntry(Connection connection, string role, string channel, string subscriptionId)
    {
        var key = new IndexKey(connection.ProjectId, role, channel);
        if (!_roleIndex.TryGetValue(key, out var set))
            return;
        set.TryRemove(new SubKey(connection.Id, subscriptionId), out _);
        if (set.IsEmpty)
            _roleIndex.TryRemove(key, out _);
    }

    private void RemoveBypassEntry(Connection connection, string channel, string subscriptionId)
    {
        var key = (connection.ProjectId, channel);
        if (!_bypassIndex.TryGetValue(key, out var set))
            return;
        set.TryRemove(new SubKey(connection.Id, subscriptionId), out _);
        if (set.IsEmpty)
            _bypassIndex.TryRemove(key, out _);
    }

    /// <summary>
    /// Every connection this event fans out to: a hash-lookup intersection against
    /// <paramref name="evt"/>'s precomputed roles (never a re-check), plus bypass connections —
    /// grouped with which of the connection's own subscription ids matched.
    /// </summary>
    public List<(Connection Connection, string[] MatchedSubscriptions)> Match(PraxyEvent evt, IReadOnlyList<string> channels)
    {
        var perConnection = new Dictionary<Guid, (Connection Connection, HashSet<string> Subscriptions)>();

        void Add(Connection conn, string subscriptionId)
        {
            if (!perConnection.TryGetValue(conn.Id, out var entry))
            {
                entry = (conn, []);
                perConnection[conn.Id] = entry;
            }
            entry.Subscriptions.Add(subscriptionId);
        }

        foreach (var channel in channels)
        {
            foreach (var role in evt.Permissions)
                if (_roleIndex.TryGetValue(new IndexKey(evt.ProjectId, role, channel), out var set))
                    foreach (var (subKey, conn) in set)
                        Add(conn, subKey.SubscriptionId);

            if (_bypassIndex.TryGetValue((evt.ProjectId, channel), out var bypassSet))
                foreach (var (subKey, conn) in bypassSet)
                    Add(conn, subKey.SubscriptionId);
        }

        return [.. perConnection.Values.Select(e => (e.Connection, e.Subscriptions.ToArray()))];
    }

    /// <summary>
    /// Flags connections holding any of <paramref name="roles"/> for lazy re-resolution rather
    /// than re-resolving eagerly (roadmap.md Phase 4). This is the rare path (user/team/membership
    /// changes, not row writes), so a plain scan over live connections is the right cost trade-off
    /// against maintaining a fourth index just for it.
    /// </summary>
    public void MarkForRevalidation(string projectId, IEnumerable<string> roles)
    {
        var roleSet = roles as ISet<string> ?? new HashSet<string>(roles);
        if (roleSet.Count == 0)
            return;
        foreach (var connection in _byId.Values)
            if (connection.ProjectId == projectId && !connection.Bypass && connection.Roles.Any(roleSet.Contains))
                connection.NeedsRevalidation = true;
    }

    /// <summary>Session revocation (roadmap.md: "sessions.delete closes that session's sockets") — also a rare path, plain scan.</summary>
    public void CloseSession(string projectId, Guid sessionId, string reason)
    {
        foreach (var connection in _byId.Values)
            if (connection.ProjectId == projectId && connection.SessionId == sessionId)
                connection.RequestClose(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, reason);
    }

    public IEnumerable<Connection> InProject(string projectId) => _byId.Values.Where(c => c.ProjectId == projectId);
}
