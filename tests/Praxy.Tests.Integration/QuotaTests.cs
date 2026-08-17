using Npgsql;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Org-level quotas (roadmap Phase 9): <c>organizations.limits</c> overrides the instance-wide
/// <c>QuotaOptions</c> defaults per dimension. Orgs are hidden in the console UI (architecture.md
/// §11), so there is no endpoint to set <c>limits</c> this phase — tests reach it the same way a
/// self-hoster or future admin tool would: directly in Postgres.
/// </summary>
public class QuotaTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Database_quota_trips_with_a_clear_error_and_does_not_touch_postgres()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.write");
        await SetOrgLimitForProjectAsync(projectId, """{"maxDatabasesPerProject": 1}""");

        var first = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/databases", projectId, apiKey: apiKey, body: new { key = "db1", name = "DB1" }));
        Assert.Equal(201, (int)first.StatusCode);

        var second = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/databases", projectId, apiKey: apiKey, body: new { key = "db2", name = "DB2" }));
        await AssertError(second, 400, ErrorTypes.GeneralResourceLimitExceeded);

        // The trip happens before CREATE SCHEMA, not as a rollback after — no orphaned schema.
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name LIKE 'px_%'", conn);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task An_unset_org_limit_falls_back_to_the_instance_default_unchanged()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.write");

        // No limits row touched — {} is what a freshly claimed instance's org already has.
        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/databases", projectId, apiKey: apiKey, body: new { key = "db1", name = "DB1" }));
        Assert.Equal(201, (int)response.StatusCode);
    }

    [Fact]
    public async Task Project_quota_trips_at_the_organization_level()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await SetOrgLimitForProjectAsync(projectId, """{"maxProjects": 1}""");

        var blocked = await Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/console/projects", operatorToken, new { name = "Second" }));
        await AssertError(blocked, 400, ErrorTypes.GeneralResourceLimitExceeded);
    }

    [Fact]
    public async Task Quotas_endpoint_surfaces_usage_against_the_effective_limit()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.write");
        await SetOrgLimitForProjectAsync(projectId, """{"maxDatabasesPerProject": 5}""");

        var create = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/databases", projectId, apiKey: apiKey, body: new { key = "db1", name = "DB1" }));
        Assert.Equal(201, (int)create.StatusCode);

        var response = await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/quotas", operatorToken));
        var body = await ReadJson(response);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(1, body.GetProperty("databasesUsed").GetInt32());
        Assert.Equal(5, body.GetProperty("databasesMax").GetInt32());
        Assert.True(body.GetProperty("projectsMax").GetInt32() > 0);
    }

    private async Task SetOrgLimitForProjectAsync(string projectId, string limitsJson)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE praxy.organizations SET limits = $1::jsonb
            WHERE id = (SELECT organization_id FROM praxy.projects WHERE id = $2)
            """, conn);
        cmd.Parameters.AddWithValue(limitsJson);
        cmd.Parameters.AddWithValue(projectId);
        var affected = await cmd.ExecuteNonQueryAsync();
        Assert.Equal(1, affected);
    }
}
