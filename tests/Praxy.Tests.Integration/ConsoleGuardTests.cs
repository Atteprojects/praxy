using Npgsql;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The console is a reserved project: its operator data must be unreachable from the
/// data plane, and it can never hold API keys. This is the guard the roadmap demands
/// a test for in Phase 0, not a comment.
/// </summary>
public class ConsoleGuardTests(PostgresContainerFixture pg) : ApiTestBase(pg)
{
    [Theory]
    [InlineData("console")]
    [InlineData("Console")]
    [InlineData("CONSOLE")]
    public async Task Data_plane_refuses_the_console_project(string projectId)
    {
        await ClaimAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
        request.Headers.Add("X-Praxy-Project", projectId);
        var response = await Client.SendAsync(request);

        await AssertError(response, 403, ErrorTypes.ProjectReserved);
    }

    [Fact]
    public async Task Data_plane_requires_a_project_header()
    {
        var response = await Client.GetAsync("/v1/ping");
        await AssertError(response, 400, ErrorTypes.GeneralArgumentInvalid);
    }

    [Fact]
    public async Task Data_plane_404s_unknown_projects()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
        request.Headers.Add("X-Praxy-Project", "no-such-project");
        var response = await Client.SendAsync(request);
        await AssertError(response, 404, ErrorTypes.ProjectNotFound);
    }

    [Fact]
    public async Task Ping_works_for_a_real_project_and_stamps_last_ping()
    {
        var (token, _) = await ClaimAsync();

        var create = await Client.SendAsync(Authed(HttpMethod.Post, "/v1/console/projects", token,
            new { name = "Pingable", projectId = "pingable" }));
        Assert.Equal(201, (int)create.StatusCode);

        var before = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/projects/pingable", token)));
        Assert.True(before.TryGetProperty("lastPingAt", out var lp) is false || lp.ValueKind is System.Text.Json.JsonValueKind.Null);

        var ping = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
        ping.Headers.Add("X-Praxy-Project", "pingable");
        var pong = await ReadJson(await Client.SendAsync(ping));
        Assert.Equal("pong", pong.GetProperty("status").GetString());

        var after = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/projects/pingable", token)));
        Assert.False(string.IsNullOrEmpty(after.GetProperty("lastPingAt").GetString()));
    }

    [Fact]
    public async Task Api_keys_for_the_console_project_are_refused_at_the_database()
    {
        await ClaimAsync();

        // Even a bug that bypasses every application-level guard hits the check constraint.
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO praxy.api_keys (id, project_id, name, secret_hash, scopes, created_at)
            VALUES (gen_random_uuid(), 'console', 'rogue', 'x', '{}', now())
            """, conn);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState); // check_violation
        Assert.Equal("ck_api_keys_project_not_console", ex.ConstraintName);
    }
}
