using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

public class RowEngineTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Create_get_list_update_delete_round_trip()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        // create — server-generated _id (UUIDv7), only changed/provided fields set.
        var created = await CreateRowAsync(projectId, apiKey, databaseId, tableId,
            new { data = new { title = "Hello", views = 1, published = false } });
        var rowId = created.GetProperty("$id").GetString()!;
        Assert.Equal("Hello", created.GetProperty("title").GetString());
        Assert.Equal(1, created.GetProperty("views").GetInt32());
        Assert.False(created.GetProperty("published").GetBoolean());
        Assert.False(string.IsNullOrEmpty(created.GetProperty("$createdAt").GetString()));
        Assert.Equal(created.GetProperty("$createdAt").GetString(), created.GetProperty("$updatedAt").GetString());

        // get
        var fetched = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows/{rowId}", projectId, apiKey: apiKey)));
        Assert.Equal(rowId, fetched.GetProperty("$id").GetString());

        // list
        var list = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(1, list.GetProperty("rows").GetArrayLength());

        // update — genuinely partial: only 'views' changes, 'title'/'published' stay put, _updatedAt moves.
        await Task.Delay(15); // clock resolution guard so _updatedAt is provably later
        var updated = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows/{rowId}", projectId, apiKey: apiKey,
            body: new { data = new { views = 2 } })));
        Assert.Equal(2, updated.GetProperty("views").GetInt32());
        Assert.Equal("Hello", updated.GetProperty("title").GetString()); // untouched
        Assert.False(updated.GetProperty("published").GetBoolean()); // untouched
        Assert.Equal(created.GetProperty("$createdAt").GetString(), updated.GetProperty("$createdAt").GetString());
        Assert.NotEqual(created.GetProperty("$updatedAt").GetString(), updated.GetProperty("$updatedAt").GetString());

        // delete
        var delete = await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows/{rowId}", projectId, apiKey: apiKey));
        Assert.Equal(204, (int)delete.StatusCode);
        var afterDelete = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows/{rowId}", projectId, apiKey: apiKey));
        await AssertError(afterDelete, 404, ErrorTypes.RowNotFound);
    }

    [Fact]
    public async Task Client_supplied_row_id_is_honored_and_duplicates_conflict()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        var customId = Guid.NewGuid().ToString("n");
        var created = await CreateRowAsync(projectId, apiKey, databaseId, tableId,
            new { rowId = customId, data = new { title = "A", views = 0, published = false } });
        Assert.Equal(customId, created.GetProperty("$id").GetString());

        var conflict = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey,
            body: new { rowId = customId, data = new { title = "B", views = 0, published = false } }));
        await AssertError(conflict, 409, ErrorTypes.RowAlreadyExists);
    }

    [Fact]
    public async Task Unknown_column_and_missing_required_field_are_rejected_with_fields()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        var unknownColumn = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey,
            body: new { data = new { nope = 1, title = "x", views = 0, published = false } }));
        var body1 = await AssertError(unknownColumn, 400, ErrorTypes.RowInvalidStructure);
        Assert.True(body1.GetProperty("fields").TryGetProperty("nope", out _));

        var missingRequired = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey,
            body: new { data = new { views = 0 } }));
        var body2 = await AssertError(missingRequired, 400, ErrorTypes.RowInvalidStructure);
        Assert.True(body2.GetProperty("fields").TryGetProperty("title", out _));
    }

    [Fact]
    public async Task Cursor_pagination_covers_every_row_exactly_once_in_order()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        for (var i = 0; i < 5; i++)
            await CreateRowAsync(projectId, apiKey, databaseId, tableId,
                new { data = new { title = $"row{i}", views = i, published = false } });

        var seen = new List<int>();
        string? cursor = null;
        for (var page = 0; page < 10 && seen.Count < 5; page++)
        {
            var queries = new List<string> { """{"method":"orderAsc","attribute":"views"}""", """{"method":"limit","values":[2]}""" };
            if (cursor is not null)
                queries.Add($$"""{"method":"cursorAfter","values":["{{cursor}}"]}""");
            var url = $"/v1/databases/{databaseId}/tables/{tableId}/rows?" +
                      string.Join("&", queries.Select(q => $"queries[]={Uri.EscapeDataString(q)}"));
            var response = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Get, url, projectId, apiKey: apiKey)));
            var rows = response.GetProperty("rows").EnumerateArray().ToArray();
            if (rows.Length == 0)
                break;
            seen.AddRange(rows.Select(r => r.GetProperty("views").GetInt32()));
            cursor = rows[^1].GetProperty("$id").GetString();
            if (rows.Length < 2)
                break;
        }

        Assert.Equal([0, 1, 2, 3, 4], seen);
    }

    [Fact]
    public async Task Exceeding_the_limit_cap_is_a_clear_400_with_fields()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        var query = """{"method":"limit","values":[500]}""";
        var url = $"/v1/databases/{databaseId}/tables/{tableId}/rows?queries[]={Uri.EscapeDataString(query)}";
        var response = await Client.SendAsync(DataPlane(HttpMethod.Get, url, projectId, apiKey: apiKey));
        var body = await AssertError(response, 400, ErrorTypes.GeneralQueryInvalid);
        Assert.True(body.GetProperty("fields").TryGetProperty("limit", out _));
    }

    [Fact]
    public async Task Search_without_a_fulltext_index_is_rejected_never_a_silent_scan()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        var query = """{"method":"search","attribute":"title","values":["hello"]}""";
        var url = $"/v1/databases/{databaseId}/tables/{tableId}/rows?queries[]={Uri.EscapeDataString(query)}";
        var response = await Client.SendAsync(DataPlane(HttpMethod.Get, url, projectId, apiKey: apiKey));
        await AssertError(response, 400, ErrorTypes.GeneralQueryInvalid);
    }

    [Fact]
    public async Task Row_security_toggle_changes_a_non_owner_sessions_reads()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        var (tokenA, userA) = await SignupAsync(projectId, "a@example.com");
        var (tokenB, userB) = await SignupAsync(projectId, "b@example.com");
        var wireA = userA.GetProperty("id").GetString()!;
        var wireB = userB.GetProperty("id").GetString()!;

        // Table-level: anyone can create; only user A can read/update at the table level (A "owns" their rows).
        await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
            body: new
            {
                permissions = new[] { "create(\"users\")", $"read(\"user:{wireA}\")", $"update(\"user:{wireA}\")" },
            }));

        var row1 = await CreateRowAsync(projectId, tokenA, null, databaseId, tableId,
            new { data = new { title = "shared with B", views = 0, published = false } });
        var row2 = await CreateRowAsync(projectId, tokenA, null, databaseId, tableId,
            new { data = new { title = "not shared", views = 0, published = false } });
        var row1Id = row1.GetProperty("$id").GetString()!;
        var row2Id = row2.GetProperty("$id").GetString()!;

        // A (named at the table level) can read; B cannot — row_security is still off.
        var aReadsRow1 = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows/{row1Id}", projectId, sessionToken: tokenA));
        Assert.Equal(200, (int)aReadsRow1.StatusCode);
        var bReadsRow1BeforeSharing = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows/{row1Id}", projectId, sessionToken: tokenB));
        await AssertError(bReadsRow1BeforeSharing, 404, ErrorTypes.RowNotFound);

        // Flip row_security on, then A shares row1 (not row2) with B via a row-level grant.
        await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
            body: new { rowSecurity = true }));
        var share = await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows/{row1Id}", projectId, sessionToken: tokenA,
            body: new { permissions = new[] { $"read(\"user:{wireB}\")" } }));
        Assert.Equal(200, (int)share.StatusCode);

        // B's reads changed: row1 (shared) now visible, row2 (never shared) still isn't.
        var bReadsRow1AfterSharing = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows/{row1Id}", projectId, sessionToken: tokenB));
        Assert.Equal(200, (int)bReadsRow1AfterSharing.StatusCode);
        var bReadsRow2 = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows/{row2Id}", projectId, sessionToken: tokenB));
        await AssertError(bReadsRow2, 404, ErrorTypes.RowNotFound);
    }

    [Fact]
    public async Task Row_permissions_are_rejected_unless_row_security_is_on()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey,
            body: new
            {
                data = new { title = "x", views = 0, published = false },
                permissions = new[] { "read(\"any\")" },
            }));
        await AssertError(response, 400, ErrorTypes.GeneralArgumentInvalid);
    }

    [Fact]
    public async Task Writes_go_through_the_outbox()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var (databaseId, tableId) = await CreateTableWithColumnsAsync(projectId, apiKey);

        var created = await CreateRowAsync(projectId, apiKey, databaseId, tableId,
            new { data = new { title = "x", views = 0, published = false } });
        var rowId = created.GetProperty("$id").GetString()!;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT type, payload FROM praxy.events WHERE project_id = $1 AND type LIKE '%.create' ORDER BY created_at DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue(projectId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var type = reader.GetString(0);
        var payload = reader.GetString(1);
        Assert.Contains($"rows.{rowId}.create", type);
        Assert.Contains(rowId, payload);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<(string DatabaseId, string TableId)> CreateTableWithColumnsAsync(string projectId, string apiKey)
    {
        var database = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Post,
            "/v1/databases", projectId, apiKey: apiKey, body: new { key = "blog", name = "Blog" })));
        var databaseId = database.GetProperty("id").GetString()!;
        var table = await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables", projectId, apiKey: apiKey, body: new { key = "posts", name = "Posts" })));
        var tableId = table.GetProperty("id").GetString()!;

        await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/string", projectId, apiKey: apiKey,
            body: new { key = "title", size = 200, required = true }));
        await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/integer", projectId, apiKey: apiKey,
            body: new { key = "views" }));
        await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/columns/boolean", projectId, apiKey: apiKey,
            body: new { key = "published" }));

        // Public create/read by default so plain API-key-driven tests don't need explicit grants.
        await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
            body: new { permissions = new[] { "create(\"any\")", "read(\"any\")", "update(\"any\")", "delete(\"any\")" } }));

        return (databaseId, tableId);
    }

    private async Task<JsonElement> CreateRowAsync(
        string projectId, string apiKey, string databaseId, string tableId, object body)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey, body: body));
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
    }

    private async Task<JsonElement> CreateRowAsync(
        string projectId, string? sessionToken, string? apiKey, string databaseId, string tableId, object body)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, sessionToken: sessionToken, apiKey: apiKey, body: body));
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
    }
}
