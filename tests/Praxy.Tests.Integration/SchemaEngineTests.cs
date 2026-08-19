using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Tables;
using Praxy.Tests.Integration.Infrastructure;
using Database = Praxy.Persistence.Entities.Database;

namespace Praxy.Tests.Integration;

public class SchemaEngineTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Owner_test_flow()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");

        // create database → metadata + CREATE SCHEMA in one transaction
        var database = await CreateDatabaseAsync(projectId, apiKey, "blog", "Blog");
        var databaseId = database.GetProperty("id").GetString()!;
        Assert.True(await SchemaExistsAsync(database));

        // create table → system columns only, available immediately
        var table = await CreateTableAsync(projectId, apiKey, databaseId, "posts", "Posts");
        var tableId = table.GetProperty("id").GetString()!;
        Assert.False(table.GetProperty("rowSecurity").GetBoolean());
        Assert.True(await TableExistsAsync(database, table));

        // add one column of every type
        var title = await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "string",
            new { key = "title", size = 200, required = true });
        await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "integer", new { key = "views" });
        await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "float", new { key = "rating" });
        await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "boolean", new { key = "published" });
        await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "datetime", new { key = "publishedAt" });
        await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "email", new { key = "authorEmail" });
        await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "url", new { key = "canonicalUrl" });
        await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "ip", new { key = "authorIp" });
        var status = await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "enum",
            new { key = "status", elements = new[] { "draft", "published", "archived" } });
        // Required but never indexed — isolates the "force required" error from "index dependency".
        var slug = await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "string",
            new { key = "slug", size = 64, required = true });

        var columns = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/databases/{databaseId}/tables/{tableId}/columns", projectId, apiKey: apiKey)));
        Assert.Equal(10, columns.GetProperty("total").GetInt32());
        Assert.All(columns.GetProperty("columns").EnumerateArray(),
            c => Assert.Equal("available", c.GetProperty("status").GetString()));

        // add a unique index and watch processing → available
        var uniqueIndexResponse = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/indexes", projectId, apiKey: apiKey,
            body: new { key = "idx_title", type = "unique", columns = new[] { "title" } }));
        Assert.Equal(201, (int)uniqueIndexResponse.StatusCode);
        var uniqueIndex = await ReadJson(uniqueIndexResponse);
        Assert.Equal("processing", uniqueIndex.GetProperty("status").GetString());
        var uniqueIndexId = uniqueIndex.GetProperty("id").GetString()!;

        var settled = await WaitForIndexStatusAsync(projectId, apiKey, databaseId, tableId, uniqueIndexId, "available");
        Assert.Equal("available", settled.GetProperty("status").GetString());
        Assert.True(await PgIndexExistsAsync(database, table, "idx_title"));

        // add a fulltext index
        var fulltextResponse = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/indexes", projectId, apiKey: apiKey,
            body: new { key = "idx_search", type = "fulltext", columns = new[] { "title" } }));
        Assert.Equal(201, (int)fulltextResponse.StatusCode);
        var fulltextId = (await ReadJson(fulltextResponse)).GetProperty("id").GetString()!;
        await WaitForIndexStatusAsync(projectId, apiKey, databaseId, tableId, fulltextId, "available");
        Assert.True(await PgIndexExistsAsync(database, table, "idx_search"));

        // dropping a required column without force is a clear error
        var slugId = slug.GetProperty("id").GetString()!;
        var dropWithoutForce = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/databases/{databaseId}/tables/{tableId}/columns/{slugId}", projectId, apiKey: apiKey));
        await AssertError(dropWithoutForce, 400, ErrorTypes.GeneralForceRequired);

        // ...but with force, and no index depending on it, it goes through.
        var dropWithForce = await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/{slugId}?force=true", projectId, apiKey: apiKey));
        Assert.Equal(204, (int)dropWithForce.StatusCode);

        // 'title' is required AND indexed — even force can't drop it out from under the index.
        var titleId = title.GetProperty("id").GetString()!;
        var dropWithForceBlocked = await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/{titleId}?force=true", projectId, apiKey: apiKey));
        await AssertError(dropWithForceBlocked, 409, ErrorTypes.IndexDependency);

        // rename a column — instant, metadata-only
        var statusId = status.GetProperty("id").GetString()!;
        var renamed = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/{statusId}", projectId, apiKey: apiKey,
            body: new { key = "state" })));
        Assert.Equal("state", renamed.GetProperty("key").GetString());
        Assert.Equal(statusId, renamed.GetProperty("id").GetString()); // same resource, address unchanged

        // set permissions (the console's "Owner only" preset: row security on, table-level create only)
        var permissions = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
            body: new { rowSecurity = true, permissions = new[] { "create(\"users\")" } })));
        Assert.True(permissions.GetProperty("rowSecurity").GetBoolean());
        Assert.Equal(["create(\"users\")"],
            permissions.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToArray());
        Assert.True(await PermsTableExistsAsync(database, table));

        // delete table — typed-name confirm on the console maps to force=true here
        var deleteWithoutForce = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/databases/{databaseId}/tables/{tableId}", projectId, apiKey: apiKey));
        await AssertError(deleteWithoutForce, 400, ErrorTypes.GeneralForceRequired);

        var deleteWithForce = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/databases/{databaseId}/tables/{tableId}?force=true", projectId, apiKey: apiKey));
        Assert.Equal(204, (int)deleteWithForce.StatusCode);
        Assert.False(await TableExistsAsync(database, table));

        var afterDelete = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/databases/{databaseId}/tables/{tableId}", projectId, apiKey: apiKey));
        await AssertError(afterDelete, 404, ErrorTypes.TableNotFound);
    }

    [Fact]
    public async Task Row_byte_budget_is_enforced_with_named_offenders()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var databaseId = (await CreateDatabaseAsync(projectId, apiKey, "db1", "DB")).GetProperty("id").GetString()!;
        var tableId = (await CreateTableAsync(projectId, apiKey, databaseId, "big", "Big")).GetProperty("id").GetString()!;

        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/string", projectId, apiKey: apiKey,
            body: new { key = "huge", size = 5000 })); // 5000 * 4 bytes >> 8000-byte budget
        var body = await AssertError(response, 400, ErrorTypes.RowSizeExceeded);
        Assert.Contains("huge", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Custom_id_and_key_validation_reject_malformed_input()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");

        var badDbKey = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/databases", projectId, apiKey: apiKey,
            body: new { key = "bad key!", name = "Bad" }));
        await AssertError(badDbKey, 400, ErrorTypes.GeneralArgumentInvalid);
    }

    [Fact]
    public async Task Names_containing_sql_metacharacters_are_stored_as_data_never_executed()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");

        const string hostileName = "Robert'); DROP TABLE praxy.projects; --";
        var database = await CreateDatabaseAsync(projectId, apiKey, "safe", hostileName);
        Assert.Equal(hostileName, database.GetProperty("name").GetString());

        // praxy.projects must be intact — the value was never interpolated into SQL.
        var projects = await ReadJson(await Client.SendAsync(
            Authed(HttpMethod.Get, "/v1/console/projects", operatorToken)));
        Assert.True(projects.GetProperty("total").GetInt32() >= 1);
    }

    [Fact]
    public async Task Missing_scope_is_a_401_and_wrong_project_is_a_404()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, readOnlyKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read");

        var create = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/databases", projectId, apiKey: readOnlyKey,
            body: new { key = "db1", name = "DB" }));
        await AssertError(create, 401, ErrorTypes.GeneralUnauthorizedScope);

        var (_, writeKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var database = await CreateDatabaseAsync(projectId, writeKey, "db1", "DB");

        var createB = await Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/console/projects", operatorToken, new { name = "Second" }));
        var projectB = (await ReadJson(createB)).GetProperty("id").GetString()!;
        var (_, keyB) = await CreateApiKeyAsync(operatorToken, projectB, "databases.read");
        var crossProject = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{database.GetProperty("id").GetString()}", projectB, apiKey: keyB));
        await AssertError(crossProject, 404, ErrorTypes.DatabaseNotFound);
    }

    [Fact]
    public async Task Console_admin_surface_mirrors_the_data_plane()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var created = await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/databases", operatorToken, new { key = "db1", name = "DB" }));
        Assert.Equal(201, (int)created.StatusCode);
        var database = await ReadJson(created);
        var databaseId = database.GetProperty("id").GetString()!;

        var table = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/databases/{databaseId}/tables", operatorToken,
            new { key = "posts", name = "Posts" })));
        var tableId = table.GetProperty("id").GetString()!;

        var column = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/databases/{databaseId}/tables/{tableId}/columns/string", operatorToken,
            new { key = "title", size = 100 })));
        Assert.Equal("title", column.GetProperty("key").GetString());

        var list = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/databases/{databaseId}/tables/{tableId}/columns", operatorToken)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Cancel_and_retry_are_rejected_on_terminal_job_states()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var databaseId = (await CreateDatabaseAsync(projectId, apiKey, "db1", "DB")).GetProperty("id").GetString()!;
        var tableId = (await CreateTableAsync(projectId, apiKey, databaseId, "posts", "Posts")).GetProperty("id").GetString()!;
        await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "string", new { key = "title", size = 50 });

        var index = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/indexes", projectId, apiKey: apiKey,
            body: new { key = "idx_title", type = "key", columns = new[] { "title" } })));
        var indexId = index.GetProperty("id").GetString()!;
        await WaitForIndexStatusAsync(projectId, apiKey, databaseId, tableId, indexId, "available");

        var jobs = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/databases/{databaseId}/jobs", projectId, apiKey: apiKey)));
        var jobId = jobs.GetProperty("jobs")[0].GetProperty("id").GetString()!;

        var cancel = await Client.SendAsync(DataPlane(
            HttpMethod.Post, $"/v1/databases/{databaseId}/jobs/{jobId}/cancel", projectId, apiKey: apiKey));
        await AssertError(cancel, 400, ErrorTypes.SchemaJobInvalidState);

        var retry = await Client.SendAsync(DataPlane(
            HttpMethod.Post, $"/v1/databases/{databaseId}/jobs/{jobId}/retry", projectId, apiKey: apiKey));
        await AssertError(retry, 400, ErrorTypes.SchemaJobInvalidState);
    }

    [Fact]
    public async Task Retry_requeues_a_failed_job_and_the_runner_reprocesses_it()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var databaseId = (await CreateDatabaseAsync(projectId, apiKey, "db1", "DB")).GetProperty("id").GetString()!;
        var tableId = (await CreateTableAsync(projectId, apiKey, databaseId, "posts", "Posts")).GetProperty("id").GetString()!;
        await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "string", new { key = "title", size = 50 });

        // Inject the index + job directly, referencing a column that doesn't physically exist, so
        // the runner's DDL deterministically fails on its very first attempt — going through the
        // create-index API here would race the runner, which can finish an empty-table index build
        // before a follow-up UPDATE has a chance to land.
        var indexId = await InjectBrokenIndexJobAsync(databaseId, tableId, "idx_title");

        var failed = await WaitForIndexStatusAsync(projectId, apiKey, databaseId, tableId, indexId, "failed");
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("error").GetString()));

        var jobs = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/databases/{databaseId}/jobs", projectId, apiKey: apiKey)));
        var jobId = jobs.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        var attemptsBefore = jobs.GetProperty("jobs")[0].GetProperty("attempts").GetInt32();

        var retry = await Client.SendAsync(DataPlane(
            HttpMethod.Post, $"/v1/databases/{databaseId}/jobs/{jobId}/retry", projectId, apiKey: apiKey));
        var retried = await ReadJson(retry);
        Assert.Equal(200, (int)retry.StatusCode);
        Assert.Equal(attemptsBefore + 1, retried.GetProperty("attempts").GetInt32());

        // Still broken, so it fails again — proving the runner actually picked the requeued job back up.
        await WaitForIndexStatusAsync(projectId, apiKey, databaseId, tableId, indexId, "failed");
        var jobAfter = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/databases/{databaseId}/jobs/{jobId}", projectId, apiKey: apiKey)));
        Assert.Equal("failed", jobAfter.GetProperty("status").GetString());
    }

    /// <summary>
    /// The exact mechanism <c>SchemaJobsService.CancelAsync</c>/<c>SchemaJobRunner</c> rely on:
    /// <c>pg_cancel_backend</c> against a captured pid surfaces as SQLSTATE 57014 on the awaiting
    /// caller. Verified directly against Postgres rather than orchestrating a real concurrent
    /// <c>CREATE INDEX CONCURRENTLY</c> block, which would make the test timing-dependent.
    /// </summary>
    [Fact]
    public async Task Pg_cancel_backend_aborts_the_targeted_connection()
    {
        using var scope = Factory.Services.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var victim = await dataSource.OpenConnectionAsync();
        var pid = (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", victim).ExecuteScalarAsync())!;

        var sleepTask = new NpgsqlCommand("SELECT pg_sleep(30)", victim).ExecuteNonQueryAsync();
        await Task.Delay(300); // let the sleep actually start before we cancel it

        await using var canceller = await dataSource.OpenConnectionAsync();
        await using var cancelCmd = new NpgsqlCommand("SELECT pg_cancel_backend($1)", canceller);
        cancelCmd.Parameters.AddWithValue(pid);
        await cancelCmd.ExecuteScalarAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => sleepTask);
        Assert.Equal("57014", ex.SqlState);
    }

    /// <summary>
    /// Database delete is the table-delete trade one level up: <c>force=true</c> required, metadata
    /// row and <c>DROP SCHEMA ... CASCADE</c> in one transaction, and every contained table evicted
    /// from the catalog cache individually — the cache is keyed by table id, so there is no
    /// "invalidate the database" shortcut.
    /// </summary>
    [Fact]
    public async Task Deleting_a_database_drops_its_schema_and_everything_in_it()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");

        var database = await CreateDatabaseAsync(projectId, apiKey, "blog", "Blog");
        var databaseId = database.GetProperty("id").GetString()!;
        var table = await CreateTableAsync(projectId, apiKey, databaseId, "posts", "Posts");
        var tableId = table.GetProperty("id").GetString()!;
        await CreateColumnAsync(projectId, apiKey, databaseId, tableId, "string", new { key = "title", size = 100 });

        // Read the physical schema name now — after the delete there is no catalog row to learn it from.
        var schemaName = await SchemaNameOfAsync(databaseId);
        Assert.True(await SchemaExistsAsync(schemaName));

        // Warm the row engine's catalog entry for this table, so the post-delete request below is
        // served from a populated cache rather than a fresh load.
        await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
            body: new { permissions = new[] { "read(\"any\")" } }));
        var rowsBefore = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey));
        Assert.Equal(200, (int)rowsBefore.StatusCode);

        // Unforced is refused loudly, and changes nothing.
        var unforced = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/databases/{databaseId}", projectId, apiKey: apiKey));
        await AssertError(unforced, 400, ErrorTypes.GeneralForceRequired);
        Assert.True(await SchemaExistsAsync(schemaName));

        var deleted = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/databases/{databaseId}?force=true", projectId, apiKey: apiKey));
        Assert.Equal(204, (int)deleted.StatusCode);

        // Both halves of the transaction landed: catalog rows gone (FK cascade), schema dropped.
        Assert.False(await SchemaExistsAsync(schemaName));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.databases WHERE id = $1::uuid", databaseId));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.tables WHERE id = $1::uuid", tableId));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.columns WHERE table_id = $1::uuid", tableId));

        var afterDelete = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/databases/{databaseId}", projectId, apiKey: apiKey));
        await AssertError(afterDelete, 404, ErrorTypes.DatabaseNotFound);

        // Miss the per-table invalidation and this is served from the warm entry above, hitting a
        // schema that no longer exists — a raw Postgres error rather than a clean 404.
        var rowsAfter = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey));
        await AssertError(rowsAfter, 404, ErrorTypes.TableNotFound);
    }

    [Fact]
    public async Task A_database_cannot_be_deleted_from_another_project()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var database = await CreateDatabaseAsync(projectId, apiKey, "blog", "Blog");
        var databaseId = database.GetProperty("id").GetString()!;
        var schemaName = await SchemaNameOfAsync(databaseId);

        var projectB = (await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/console/projects", operatorToken, new { name = "Second" }))))
            .GetProperty("id").GetString()!;
        var (_, keyB) = await CreateApiKeyAsync(operatorToken, projectB, "databases.read", "databases.write");

        var crossProject = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/databases/{databaseId}?force=true", projectB, apiKey: keyB));
        await AssertError(crossProject, 404, ErrorTypes.DatabaseNotFound);
        Assert.True(await SchemaExistsAsync(schemaName));

        // Same 404 through the console surface, where the operator owns both projects.
        var crossProjectConsole = await Client.SendAsync(Authed(
            HttpMethod.Delete, $"/v1/console/projects/{projectB}/databases/{databaseId}?force=true", operatorToken));
        await AssertError(crossProjectConsole, 404, ErrorTypes.DatabaseNotFound);
        Assert.True(await SchemaExistsAsync(schemaName));
    }

    /// <summary>
    /// Quota is a live <c>count(*)</c> over <c>praxy.databases</c>, so a delete frees its slot with
    /// no bookkeeping of its own — this pins that, at the boundary where it matters.
    /// </summary>
    [Fact]
    public async Task Deleting_a_database_frees_its_quota_slot()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        await SetMaxDatabasesAsync(projectId, 1);

        var databaseId = (await CreateDatabaseAsync(projectId, apiKey, "db1", "DB1")).GetProperty("id").GetString()!;

        var atLimit = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/databases", projectId, apiKey: apiKey, body: new { key = "db2", name = "DB2" }));
        await AssertError(atLimit, 400, ErrorTypes.GeneralResourceLimitExceeded);

        var deleted = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/databases/{databaseId}?force=true", projectId, apiKey: apiKey));
        Assert.Equal(204, (int)deleted.StatusCode);

        // The freed slot is immediately reusable, and the old key is too.
        await CreateDatabaseAsync(projectId, apiKey, "db1", "DB1 again");
    }

    [Fact]
    public async Task Console_delete_writes_an_audit_entry_and_removes_the_database()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();

        var database = await ReadJson(await Client.SendAsync(Authed(HttpMethod.Post,
            $"/v1/console/projects/{projectId}/databases", operatorToken, new { key = "db1", name = "DB" })));
        var databaseId = database.GetProperty("id").GetString()!;
        var schemaName = await SchemaNameOfAsync(databaseId);

        var unforced = await Client.SendAsync(Authed(
            HttpMethod.Delete, $"/v1/console/projects/{projectId}/databases/{databaseId}", operatorToken));
        await AssertError(unforced, 400, ErrorTypes.GeneralForceRequired);

        var deleted = await Client.SendAsync(Authed(
            HttpMethod.Delete, $"/v1/console/projects/{projectId}/databases/{databaseId}?force=true", operatorToken));
        Assert.Equal(204, (int)deleted.StatusCode);
        Assert.False(await SchemaExistsAsync(schemaName));

        Assert.Equal(1L, await ScalarAsync(
            "SELECT count(*) FROM praxy.audit_log WHERE action = 'databases.delete' AND resource = $1",
            $"database/{databaseId}"));

        var list = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/databases", operatorToken)));
        Assert.Equal(0, list.GetProperty("total").GetInt32());
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<JsonElement> CreateDatabaseAsync(string projectId, string apiKey, string key, string name)
    {
        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/databases", projectId, apiKey: apiKey, body: new { key, name }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return body;
    }

    private async Task<JsonElement> CreateTableAsync(
        string projectId, string apiKey, string databaseId, string key, string name)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables", projectId, apiKey: apiKey, body: new { key, name }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return body;
    }

    private async Task<JsonElement> CreateColumnAsync(
        string projectId, string apiKey, string databaseId, string tableId, string type, object requestBody)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/{type}", projectId, apiKey: apiKey, body: requestBody));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return body;
    }

    private async Task<JsonElement> WaitForIndexStatusAsync(
        string projectId, string apiKey, string databaseId, string tableId, string indexId, string targetStatus)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var response = await Client.SendAsync(DataPlane(HttpMethod.Get,
                $"/v1/databases/{databaseId}/tables/{tableId}/indexes/{indexId}", projectId, apiKey: apiKey));
            var body = await ReadJson(response);
            var status = body.GetProperty("status").GetString();
            if (status == targetStatus)
                return body;
            if (status == "failed" && targetStatus != "failed")
                throw new Exception($"Index failed unexpectedly: {body.GetProperty("error").GetString()}");
            await Task.Delay(150);
        }
        throw new TimeoutException($"Index did not reach status '{targetStatus}' in time.");
    }

    /// <summary>
    /// Writes an index + <c>create_index</c> job straight into the catalog, pointed at a column
    /// physical name that doesn't exist, guaranteeing the runner's DDL fails on its first attempt.
    /// Returns the wire index id.
    /// </summary>
    private async Task<string> InjectBrokenIndexJobAsync(string databaseId, string tableId, string indexKey)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PraxyDb>();

        var databaseGuid = Guid.Parse(databaseId);
        var tableGuid = Guid.Parse(tableId);
        var database = await db.Databases.FirstAsync(d => d.Id == databaseGuid);
        var table = await db.Tables.FirstAsync(t => t.Id == tableGuid);

        var indexId = Ids.NewUuid();
        var physicalName = PhysicalNaming.IndexName(indexKey, indexId);
        db.Indexes.Add(new IndexDef
        {
            Id = indexId,
            TableId = table.Id,
            Key = indexKey,
            Type = IndexesService.TypeKey,
            Columns = ["title"],
            Orders = ["asc"],
            PhysicalName = physicalName,
            Status = "processing",
        });
        db.SchemaJobs.Add(new SchemaJob
        {
            Id = Ids.NewUuid(),
            DatabaseId = database.Id,
            TableId = table.Id,
            Kind = SchemaJobKinds.CreateIndex,
            Payload = JsonSerializer.Serialize(new CreateIndexJobPayload(
                IndexId: Ids.Wire(indexId), SchemaName: database.SchemaName, TablePhysicalName: table.PhysicalName,
                IndexPhysicalName: physicalName, Unique: false, Fulltext: false,
                ColumnsPhysical: ["does_not_exist"], Orders: ["asc"], FulltextColumnPhysical: null)),
            Status = "queued",
        });
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<SchemaJobSignal>().Notify();
        return Ids.Wire(indexId);
    }

    /// <summary>The physical schema name, read from the catalog row — never on the wire.</summary>
    private async Task<string> SchemaNameOfAsync(string databaseId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT schema_name FROM praxy.databases WHERE id = $1::uuid", conn);
        cmd.Parameters.AddWithValue(databaseId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Existence by name rather than by catalog lookup: once the database row is deleted the
    /// subquery in the overload below yields NULL, which would report "gone" whether or not the
    /// schema was actually dropped.
    /// </summary>
    private async Task<bool> SchemaExistsAsync(string schemaName)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = $1)", conn);
        cmd.Parameters.AddWithValue(schemaName);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<object?> ScalarAsync(string sql, params object[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var parameter in parameters)
            cmd.Parameters.AddWithValue(parameter);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task SetMaxDatabasesAsync(string projectId, int max)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE praxy.organizations SET limits = $1::jsonb
            WHERE id = (SELECT organization_id FROM praxy.projects WHERE id = $2)
            """, conn);
        cmd.Parameters.AddWithValue($$"""{"maxDatabasesPerProject": {{max}}}""");
        cmd.Parameters.AddWithValue(projectId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> SchemaExistsAsync(JsonElement database)
    {
        // The schema name isn't on the wire, so read it straight from the catalog — this also
        // doubles as the "px_<hex32> was actually created" proof for the create-database step.
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = " +
            "(SELECT schema_name FROM praxy.databases WHERE id = $1::uuid))", conn);
        cmd.Parameters.AddWithValue(database.GetProperty("id").GetString()!);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<bool> TableExistsAsync(JsonElement database, JsonElement table)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT EXISTS (
              SELECT 1 FROM information_schema.tables
              WHERE table_schema = (SELECT schema_name FROM praxy.databases WHERE id = $1::uuid)
                AND table_name = (SELECT physical_name FROM praxy.tables WHERE id = $2::uuid))
            """, conn);
        cmd.Parameters.AddWithValue(database.GetProperty("id").GetString()!);
        cmd.Parameters.AddWithValue(table.GetProperty("id").GetString()!);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<bool> PermsTableExistsAsync(JsonElement database, JsonElement table)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT EXISTS (
              SELECT 1 FROM information_schema.tables
              WHERE table_schema = (SELECT schema_name FROM praxy.databases WHERE id = $1::uuid)
                AND table_name = (SELECT physical_name FROM praxy.tables WHERE id = $2::uuid) || '__perms')
            """, conn);
        cmd.Parameters.AddWithValue(database.GetProperty("id").GetString()!);
        cmd.Parameters.AddWithValue(table.GetProperty("id").GetString()!);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<bool> PgIndexExistsAsync(JsonElement database, JsonElement table, string indexKey)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT EXISTS (
              SELECT 1 FROM pg_indexes
              WHERE schemaname = (SELECT schema_name FROM praxy.databases WHERE id = $1::uuid)
                AND tablename = (SELECT physical_name FROM praxy.tables WHERE id = $2::uuid)
                AND indexname = (SELECT physical_name FROM praxy.indexes
                                  WHERE table_id = $2::uuid AND key = $3))
            """, conn);
        cmd.Parameters.AddWithValue(database.GetProperty("id").GetString()!);
        cmd.Parameters.AddWithValue(table.GetProperty("id").GetString()!);
        cmd.Parameters.AddWithValue(indexKey);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
