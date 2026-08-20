using System.Text.Json;
using Npgsql;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Post-v0.1.0 gap #5: project rename/delete, database rename, membership role edit — three
/// console operations that had a read path but never a write path for one field. Project delete is
/// the sharpest case: physical Postgres schemas and function containers have no FK to the project
/// row, so they need the same per-resource <c>DeleteAsync</c> the console already uses for one
/// database/function at a time, looped, before the project row itself goes.
/// </summary>
public class ProjectAdminTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Renaming_a_project_updates_the_name_and_leaves_the_id_untouched()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var renamed = await Client.SendAsync(Authed(HttpMethod.Patch, $"/v1/console/projects/{projectId}",
            operatorToken, new { name = "Renamed" }));
        Assert.Equal(200, (int)renamed.StatusCode);
        var body = await ReadJson(renamed);
        Assert.Equal("Renamed", body.GetProperty("name").GetString());
        Assert.Equal(projectId, body.GetProperty("id").GetString());

        var fetched = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/projects/{projectId}", operatorToken)));
        Assert.Equal("Renamed", fetched.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Renaming_a_database_updates_the_name_and_leaves_key_and_schema_untouched()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, key) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var database = await CreateDatabaseAsync(projectId, key, "blog", "Blog");
        var databaseId = database.GetProperty("id").GetString()!;
        var schemaName = await SchemaNameOfAsync(databaseId);

        var renamed = await Client.SendAsync(DataPlane(HttpMethod.Patch, $"/v1/databases/{databaseId}", projectId,
            apiKey: key, body: new { name = "Renamed DB" }));
        Assert.Equal(200, (int)renamed.StatusCode);
        var body = await ReadJson(renamed);
        Assert.Equal("Renamed DB", body.GetProperty("name").GetString());
        Assert.Equal("blog", body.GetProperty("key").GetString());
        Assert.Equal(schemaName, await SchemaNameOfAsync(databaseId));

        // The console-admin PATCH is the same operation, reachable a second way, symmetric with
        // every other database/table pair in this codebase.
        var viaConsole = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/databases/{databaseId}", operatorToken,
            new { name = "Renamed again" }));
        Assert.Equal(200, (int)viaConsole.StatusCode);
        Assert.Equal("Renamed again", (await ReadJson(viaConsole)).GetProperty("name").GetString());
        Assert.Equal(schemaName, await SchemaNameOfAsync(databaseId));
    }

    [Fact]
    public async Task Deleting_a_project_without_force_is_a_clean_400_and_changes_nothing()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var response = await Client.SendAsync(
            Authed(HttpMethod.Delete, $"/v1/console/projects/{projectId}", operatorToken));
        await AssertError(response, 400, ErrorTypes.GeneralForceRequired);

        var stillThere = await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/projects/{projectId}", operatorToken));
        Assert.Equal(200, (int)stillThere.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_project_drops_every_database_schema_and_every_function_row()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, key) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var database = await CreateDatabaseAsync(projectId, key, "blog", "Blog");
        var databaseId = database.GetProperty("id").GetString()!;
        var schemaName = await SchemaNameOfAsync(databaseId);
        Assert.True(await SchemaExistsAsync(schemaName));

        // No Docker build here — this proves the metadata + eviction call happen, not the real
        // container lifecycle (that's FunctionTests' real-Docker end-to-end coverage). Evicting an
        // *active* deployment's container is one `if` in FunctionsService.DeleteAsync
        // (`pool.EvictAsync` when `ActiveDeploymentId` is set) identical to what a lone
        // `DELETE /functions/{id}` already exercises — a bare function with no deployment proves
        // the loop reaches every function and removes its row, which is what this test is for.
        await CreateFunctionAsync(operatorToken, projectId, "greeter");

        var deleted = await Client.SendAsync(
            Authed(HttpMethod.Delete, $"/v1/console/projects/{projectId}?force=true", operatorToken));
        Assert.Equal(204, (int)deleted.StatusCode);

        Assert.False(await SchemaExistsAsync(schemaName));
        Assert.Equal(0L, await ScalarLongAsync(
            "SELECT COUNT(*) FROM praxy.databases WHERE project_id = $1", projectId));
        Assert.Equal(0L, await ScalarLongAsync(
            "SELECT COUNT(*) FROM praxy.functions WHERE project_id = $1", projectId));
        Assert.Equal(0L, await ScalarLongAsync(
            "SELECT COUNT(*) FROM praxy.projects WHERE id = $1", projectId));

        var gone = await Client.SendAsync(Authed(HttpMethod.Get, $"/v1/console/projects/{projectId}", operatorToken));
        await AssertError(gone, 404, ErrorTypes.ProjectNotFound);
    }

    [Fact]
    public async Task A_second_operator_cannot_rename_or_delete_another_orgs_project()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (otherToken, _) = await CreateSecondOperatorAsync();

        var rename = await Client.SendAsync(Authed(HttpMethod.Patch, $"/v1/console/projects/{projectId}",
            otherToken, new { name = "Hijacked" }));
        await AssertError(rename, 404, ErrorTypes.ProjectNotFound);

        var delete = await Client.SendAsync(
            Authed(HttpMethod.Delete, $"/v1/console/projects/{projectId}?force=true", otherToken));
        await AssertError(delete, 404, ErrorTypes.ProjectNotFound);

        // Untouched by either attempt, seen through the eyes of an operator who can actually reach it.
        var stillThere = await Client.SendAsync(
            Authed(HttpMethod.Get, $"/v1/console/projects/{projectId}", operatorToken));
        Assert.Equal(200, (int)stillThere.StatusCode);
    }

    [Fact]
    public async Task Editing_membership_roles_updates_the_list_and_the_members_resolved_roles()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (memberToken, user) = await SignupAsync(projectId, "member@praxy.test");
        var userId = user.GetProperty("id").GetString()!;

        var team = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/teams", operatorToken, new { name = "Eng" })));
        var teamId = team.GetProperty("id").GetString()!;

        var membership = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/teams/{teamId}/memberships", operatorToken,
            new { userId, roles = new[] { "member" } })));
        var membershipId = membership.GetProperty("id").GetString()!;

        Assert.Contains($"team:{teamId}/member", await RolesAsync(projectId, memberToken));

        var updated = await Client.SendAsync(Authed(HttpMethod.Patch,
            $"/v1/console/projects/{projectId}/teams/{teamId}/memberships/{membershipId}", operatorToken,
            new { roles = new[] { "owner" } }));
        Assert.Equal(200, (int)updated.StatusCode);
        var updatedRoles = (await ReadJson(updated)).GetProperty("roles").EnumerateArray()
            .Select(r => r.GetString()).ToList();
        Assert.Equal(["owner"], updatedRoles);

        var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/teams/{teamId}/memberships", operatorToken)));
        Assert.Equal("owner", list.GetProperty("memberships")[0].GetProperty("roles")[0].GetString());

        var roles = await RolesAsync(projectId, memberToken);
        Assert.Contains($"team:{teamId}/owner", roles);
        Assert.DoesNotContain($"team:{teamId}/member", roles);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<JsonElement> CreateDatabaseAsync(string projectId, string apiKey, string key, string name)
    {
        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/databases", projectId, apiKey: apiKey, body: new { key, name }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return body;
    }

    private async Task<string> CreateFunctionAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/functions", operatorToken,
            new { key, name = key, runtime = "node", entrypoint = "index.js", timeoutSeconds = 15 }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> SchemaNameOfAsync(string databaseId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT schema_name FROM praxy.databases WHERE id = $1::uuid", conn);
        cmd.Parameters.AddWithValue(databaseId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<bool> SchemaExistsAsync(string schemaName)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = $1)", conn);
        cmd.Parameters.AddWithValue(schemaName);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> ScalarLongAsync(string sql, params object[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var parameter in parameters)
            cmd.Parameters.AddWithValue(parameter);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
