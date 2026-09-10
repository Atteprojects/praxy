using System.Text.Json;
using Npgsql;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// What an operator hits after <c>DeploymentImageSweeper</c> has reclaimed a deployment's image:
/// the row stays <c>ready</c> (its build log and commit are still worth reading) but its
/// <c>image_tag</c> is gone, so activating it can only fail.
///
/// <para>No Docker build here on purpose. The reclaimed state is exactly "a ready row with no
/// image tag", which is cheap to write directly and lets these assert the API's behaviour in that
/// state rather than spending two real Next.js builds to arrive at it.</para>
/// </summary>
public class DeploymentImageReclaimTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Activating_a_site_deployment_whose_image_was_reclaimed_is_refused_by_its_own_error_type()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "reclaimed");
        var deploymentId = await InsertReclaimedDeploymentAsync(
            "site_deployments", "site_id", siteId, projectId);

        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites/{siteId}/deployments/{deploymentId}/activate",
            operatorToken));

        Assert.Equal(400, (int)response.StatusCode);
        var body = await ReadJson(response);
        // Not site_invalid_deployment_state: that one's message is "only a 'ready' deployment can be
        // activated (this one is 'ready')", which tells an operator nothing they can act on.
        Assert.Equal("site_deployment_image_reclaimed", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Activating_a_function_deployment_whose_image_was_reclaimed_leaves_the_function_on_its_previous_deployment()
    {
        // The regression this exists for: FunctionsService.ActivateAsync checked only Status, never
        // ImageTag, so this call used to return 200 and repoint ActiveDeploymentId at a deployment
        // that could only fail later, at invoke time. The status code is half the assertion; that
        // active_deployment_id did not move is the other half.
        var (operatorToken, projectId) = await SetupProjectAsync();
        var functionId = await CreateFunctionAsync(operatorToken, projectId, "reclaimed");
        var deploymentId = await InsertReclaimedDeploymentAsync(
            "function_deployments", "function_id", functionId, projectId);

        var before = await ActiveDeploymentIdAsync("functions", functionId);

        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions/{functionId}/deployments/{deploymentId}/activate",
            operatorToken));

        Assert.Equal(400, (int)response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("function_deployment_image_reclaimed", body.GetProperty("type").GetString());
        Assert.Equal(before, await ActiveDeploymentIdAsync("functions", functionId));
    }

    [Fact]
    public async Task A_reclaimed_deployment_is_still_listed_with_its_build_history_intact()
    {
        // Reclaiming an image must not erase the deployment. The commit and build log are how an
        // operator works out what shipped when, long after the image itself stops being useful.
        var (operatorToken, projectId) = await SetupProjectAsync();
        var siteId = await CreateSiteAsync(operatorToken, projectId, "history");
        var deploymentId = await InsertReclaimedDeploymentAsync(
            "site_deployments", "site_id", siteId, projectId);

        var response = await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/sites/{siteId}/deployments/{deploymentId}", operatorToken));

        Assert.Equal(200, (int)response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("ready", body.GetProperty("status").GetString());
        // Absent, not null: Program.cs serializes WhenWritingNull, so a null-valued property is
        // omitted from the wire entirely — the exact shape docs/openapi/v1.json's nullability
        // transformer exists to describe, and the one an SDK generator gets wrong by default.
        Assert.False(body.TryGetProperty("imageTag", out _));
        Assert.Equal("built ok\n", body.GetProperty("buildLog").GetString());
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>A successful build whose image is gone: <c>ready</c>, with a build log, and no image tag.</summary>
    private async Task<string> InsertReclaimedDeploymentAsync(
        string table, string parentColumn, string parentId, string projectId)
    {
        var id = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO praxy.{table}
                (id, {parentColumn}, project_id, source_size_bytes, status, build_log, image_tag,
                 source, created_at, updated_at)
            VALUES (@id, @parent, @project, 0, 'ready', 'built ok
            ', NULL, 'upload', now(), now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("parent", Guid.Parse(parentId));
        cmd.Parameters.AddWithValue("project", projectId);
        await cmd.ExecuteNonQueryAsync();
        return Praxy.Core.Ids.Wire(id);
    }

    private async Task<Guid?> ActiveDeploymentIdAsync(string table, string id)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT active_deployment_id FROM praxy.{table} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", Guid.Parse(id));
        return await cmd.ExecuteScalarAsync() as Guid?;
    }

    private async Task<string> CreateSiteAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/sites", operatorToken,
            new { key, name = key, rootDirectory = "" }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateFunctionAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new
            {
                key, name = key, runtime = "node", entrypoint = "index.js", timeoutSeconds = 15,
                events = Array.Empty<string>(), schedule = (string?)null, execute = Array.Empty<string>(),
            }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }
}
