using Praxy.Events;
using Praxy.Realtime;

namespace Praxy.Tests.Unit;

public class ConnectionRegistryTests
{
    private const string Project = "proj1";

    private static Connection NewConnection(string[]? roles = null, bool bypass = false, Guid? sessionId = null) =>
        new() { ProjectId = Project, Roles = roles ?? [], Bypass = bypass, SessionId = sessionId };

    private static void Subscribe(ConnectionRegistry registry, Connection connection, string subscriptionId, params string[] channels)
    {
        connection.Subscriptions[subscriptionId] = channels;
        registry.Reindex(connection);
    }

    private static PraxyEvent RowEvent(string type, params string[] permissions) =>
        new("evt1", DateTimeOffset.UtcNow, Project, type, permissions, null);

    [Fact]
    public void A_matching_role_and_channel_delivers_the_event()
    {
        var registry = new ConnectionRegistry();
        var conn = NewConnection(["users"]);
        registry.TryRegister(conn, maxConnectionsPerProject: 10);
        Subscribe(registry, conn, "sub1", "databases.db.tables.t.rows");

        var matches = registry.Match(RowEvent("databases.db.tables.t.rows.row1.create", "users"), ["databases.db.tables.t.rows"]);

        var match = Assert.Single(matches);
        Assert.Equal(conn.Id, match.Connection.Id);
        Assert.Equal(["sub1"], match.MatchedSubscriptions);
    }

    [Fact]
    public void No_matching_role_means_no_delivery_not_an_error()
    {
        var registry = new ConnectionRegistry();
        var conn = NewConnection(["users"]);
        registry.TryRegister(conn, maxConnectionsPerProject: 10);
        Subscribe(registry, conn, "sub1", "databases.db.tables.t.rows");

        var matches = registry.Match(RowEvent("databases.db.tables.t.rows.row1.create", "team:x"), ["databases.db.tables.t.rows"]);

        Assert.Empty(matches);
    }

    [Fact]
    public void Subscribing_to_one_rows_channel_only_matches_that_rows_events()
    {
        var registry = new ConnectionRegistry();
        var conn = NewConnection(["users"]);
        registry.TryRegister(conn, maxConnectionsPerProject: 10);
        Subscribe(registry, conn, "sub1", "databases.db.tables.t.rows.row1");

        var otherRow = registry.Match(RowEvent("databases.db.tables.t.rows.row2.create", "users"),
            ["databases.db.tables.t.rows", "databases.db.tables.t.rows.row2", "databases.db.tables.t.rows.create"]);
        Assert.Empty(otherRow);

        var sameRow = registry.Match(RowEvent("databases.db.tables.t.rows.row1.update", "users"),
            ["databases.db.tables.t.rows", "databases.db.tables.t.rows.row1", "databases.db.tables.t.rows.update"]);
        Assert.Single(sameRow);
    }

    [Fact]
    public void Bypass_connections_match_regardless_of_permissions()
    {
        var registry = new ConnectionRegistry();
        var conn = NewConnection(bypass: true);
        registry.TryRegister(conn, maxConnectionsPerProject: 10);
        Subscribe(registry, conn, "sub1", "databases.db.tables.t.rows");

        var matches = registry.Match(RowEvent("databases.db.tables.t.rows.row1.create", "team:some-team-nobody-has"),
            ["databases.db.tables.t.rows"]);

        Assert.Single(matches);
    }

    [Fact]
    public void Unsubscribe_removes_the_connection_from_future_matches()
    {
        var registry = new ConnectionRegistry();
        var conn = NewConnection(["users"]);
        registry.TryRegister(conn, maxConnectionsPerProject: 10);
        Subscribe(registry, conn, "sub1", "databases.db.tables.t.rows");

        conn.Subscriptions.Remove("sub1");
        registry.Reindex(conn);

        var matches = registry.Match(RowEvent("databases.db.tables.t.rows.row1.create", "users"), ["databases.db.tables.t.rows"]);
        Assert.Empty(matches);
    }

    [Fact]
    public void Role_change_via_reindex_moves_the_connection_to_the_new_roles_index()
    {
        var registry = new ConnectionRegistry();
        var conn = NewConnection(["users"]);
        registry.TryRegister(conn, maxConnectionsPerProject: 10);
        Subscribe(registry, conn, "sub1", "teams.t1");

        Assert.Empty(registry.Match(RowEvent("teams.t1.update", "team:t1"), ["teams.t1"]));

        conn.Roles = ["users", "team:t1"];
        registry.Reindex(conn);

        Assert.Single(registry.Match(RowEvent("teams.t1.update", "team:t1"), ["teams.t1"]));

        // Dropping the role again removes the index entry rather than leaving it stale.
        conn.Roles = ["users"];
        registry.Reindex(conn);
        Assert.Empty(registry.Match(RowEvent("teams.t1.update", "team:t1"), ["teams.t1"]));
    }

    [Fact]
    public void Connection_quota_rejects_the_registration_over_the_cap()
    {
        var registry = new ConnectionRegistry();
        Assert.True(registry.TryRegister(NewConnection(), maxConnectionsPerProject: 1));
        Assert.False(registry.TryRegister(NewConnection(), maxConnectionsPerProject: 1));
        Assert.Equal(1, registry.CountForProject(Project));
    }

    [Fact]
    public void Unregister_frees_the_quota_slot()
    {
        var registry = new ConnectionRegistry();
        var conn = NewConnection();
        Assert.True(registry.TryRegister(conn, maxConnectionsPerProject: 1));
        registry.Unregister(conn);
        Assert.Equal(0, registry.CountForProject(Project));
        Assert.True(registry.TryRegister(NewConnection(), maxConnectionsPerProject: 1));
    }

    [Fact]
    public void Mark_for_revalidation_flags_only_connections_holding_the_role()
    {
        var registry = new ConnectionRegistry();
        var affected = NewConnection(["users", "team:t1"]);
        var unaffected = NewConnection(["users"]);
        var bypass = NewConnection(bypass: true);
        registry.TryRegister(affected, 10);
        registry.TryRegister(unaffected, 10);
        registry.TryRegister(bypass, 10);

        registry.MarkForRevalidation(Project, ["team:t1"]);

        Assert.True(affected.NeedsRevalidation);
        Assert.False(unaffected.NeedsRevalidation);
        Assert.False(bypass.NeedsRevalidation);
    }

    [Fact]
    public void Close_session_only_requests_close_for_that_sessions_connections()
    {
        var registry = new ConnectionRegistry();
        var sessionId = Guid.NewGuid();
        var target = NewConnection(sessionId: sessionId);
        var other = NewConnection(sessionId: Guid.NewGuid());
        registry.TryRegister(target, 10);
        registry.TryRegister(other, 10);

        registry.CloseSession(Project, sessionId, "Session revoked.");

        Assert.Equal("Session revoked.", target.PendingCloseReason);
        Assert.Null(other.PendingCloseReason);
    }
}
