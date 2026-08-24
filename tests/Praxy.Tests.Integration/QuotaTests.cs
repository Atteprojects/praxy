using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Tables.Quotas;
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

    /// <summary>
    /// Sites Phase 2: <c>QuotaService.EnsurePreviewQuotaAsync</c> is checked from
    /// <c>SiteProxyMiddleware</c>'s cold-start path, not a DDL endpoint like every other quota
    /// dimension here — exercised directly against the service (real Postgres, no Docker needed:
    /// deployment rows are seeded straight into the database, bypassing the build pipeline, since
    /// this check only ever reads <c>site_deployments</c>/<c>sites</c>, never a container).
    /// </summary>
    [Fact]
    public async Task Preview_container_quota_trips_once_the_configured_max_is_already_running()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "previewq");
        var dep1 = await InsertReadyDeploymentAsync(siteId);
        await SetOrgLimitForProjectAsync(projectId, """{"maxPreviewContainersPerProject": 1}""");

        using var scope = Factory.Services.CreateScope();
        var quotas = scope.ServiceProvider.GetRequiredService<QuotaService>();

        // Nothing running yet — starting the very first preview is always allowed regardless of max.
        await quotas.EnsurePreviewQuotaAsync(projectId, [], CancellationToken.None);

        // dep1 already tracked as running — a second concurrent preview for the same project trips.
        await Assert.ThrowsAsync<PraxyException>(() =>
            quotas.EnsurePreviewQuotaAsync(projectId, [dep1], CancellationToken.None));
    }

    [Fact]
    public async Task Preview_container_quota_does_not_count_the_sites_own_active_deployment()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "previewq2");
        var activeDeploymentId = await InsertReadyDeploymentAsync(siteId);
        await SetSiteActiveDeploymentAsync(siteId, activeDeploymentId);
        await SetOrgLimitForProjectAsync(projectId, """{"maxPreviewContainersPerProject": 1}""");

        using var scope = Factory.Services.CreateScope();
        var quotas = scope.ServiceProvider.GetRequiredService<QuotaService>();

        // Even though the active deployment is "tracked" (it would be, in the real registry), it's
        // always-on production, not a preview — it must never count against the preview cap.
        await quotas.EnsurePreviewQuotaAsync(projectId, [activeDeploymentId], CancellationToken.None);
    }

    private async Task<string> CreateSiteAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites", operatorToken, new { key, name = key, rootDirectory = "" }));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("id").GetString()!;
    }

    /// <summary>Seeds a <c>ready</c> deployment row directly — no Docker build needed for a quota-only check.</summary>
    private async Task<Guid> InsertReadyDeploymentAsync(string siteId)
    {
        var deploymentId = Ids.NewUuid();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO praxy.site_deployments
                (id, site_id, project_id, source_size_bytes, status, build_log, image_tag, created_at, updated_at)
            SELECT $1, id, project_id, 0, 'ready', '', 'test:latest', now(), now()
            FROM praxy.sites WHERE id = $2
            """, conn);
        cmd.Parameters.AddWithValue(deploymentId);
        cmd.Parameters.AddWithValue(Guid.Parse(siteId));
        var affected = await cmd.ExecuteNonQueryAsync();
        Assert.Equal(1, affected);
        return deploymentId;
    }

    private async Task SetSiteActiveDeploymentAsync(string siteId, Guid deploymentId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE praxy.sites SET active_deployment_id = $1 WHERE id = $2", conn);
        cmd.Parameters.AddWithValue(deploymentId);
        cmd.Parameters.AddWithValue(Guid.Parse(siteId));
        var affected = await cmd.ExecuteNonQueryAsync();
        Assert.Equal(1, affected);
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
