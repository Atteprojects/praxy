using System.Text.Json;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Relationships Phase 1 (docs/research/table-relationships.md, docs/handoff/relationships-phase-1-prompt.md):
/// the scalar/array `relationship` column type against a real Postgres instance — the actual FK,
/// the async existence pre-pass, one-to-one via a plain `unique` index, and basic query support.
/// Phase 2 (docs/handoff/relationships-phase-2-prompt.md) extends this file with delete-time
/// integrity: the scalar FK's 23503 catch, the array case's application-level pre-check, and the
/// table-delete `relationship_dependency` gate.
/// </summary>
public class RelationshipEngineTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Scalar_relationship_rejects_a_nonexistent_target_and_reads_back_the_wire_id()
    {
        var (projectId, apiKey, databaseId, authorsId, postsId) = await SetupAsync();

        var fakeAuthorId = Guid.NewGuid().ToString("n");
        var rejected = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{postsId}/rows", projectId, apiKey: apiKey,
            body: new { data = new { title = "Ghost", authorId = fakeAuthorId } }));
        var rejectedBody = await AssertError(rejected, 400, ErrorTypes.RelationshipTargetNotFound);
        Assert.True(rejectedBody.GetProperty("fields").TryGetProperty("authorId", out _));

        var authorId = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Ada" } })).GetProperty("$id").GetString()!;

        var post = await CreateRowAsync(projectId, apiKey, databaseId, postsId,
            new { data = new { title = "Hello", authorId } });
        Assert.Equal(authorId, post.GetProperty("authorId").GetString());
        Assert.Matches("^[0-9a-f]{32}$", post.GetProperty("authorId").GetString()!);

        // update path goes through the same existence pre-pass
        var badUpdate = await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{postsId}/rows/{post.GetProperty("$id").GetString()}", projectId,
            apiKey: apiKey, body: new { data = new { authorId = fakeAuthorId } }));
        await AssertError(badUpdate, 400, ErrorTypes.RelationshipTargetNotFound);
    }

    [Fact]
    public async Task Array_relationship_rejects_when_any_id_is_missing_and_accepts_when_all_present()
    {
        var (projectId, apiKey, databaseId, authorsId, postsId) = await SetupAsync();
        var a1 = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Ada" } })).GetProperty("$id").GetString()!;
        var a2 = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Grace" } })).GetProperty("$id").GetString()!;
        var fake = Guid.NewGuid().ToString("n");

        var rejected = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{postsId}/rows", projectId, apiKey: apiKey,
            body: new { data = new { title = "Team post", authorId = a1, coAuthorIds = new[] { a2, fake } } }));
        await AssertError(rejected, 400, ErrorTypes.RelationshipTargetNotFound);

        var post = await CreateRowAsync(projectId, apiKey, databaseId, postsId,
            new { data = new { title = "Team post", authorId = a1, coAuthorIds = new[] { a1, a2 } } });
        var coAuthorIds = post.GetProperty("coAuthorIds").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal([a1, a2], coAuthorIds);
    }

    [Fact]
    public async Task Scalar_relationship_plus_a_unique_index_behaves_as_one_to_one()
    {
        var (projectId, apiKey, databaseId, authorsId, postsId) = await SetupAsync();

        var indexResponse = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{postsId}/indexes", projectId, apiKey: apiKey,
            body: new { key = "idx_author_unique", type = "unique", columns = new[] { "authorId" } }));
        Assert.Equal(201, (int)indexResponse.StatusCode);
        var indexId = (await ReadJson(indexResponse)).GetProperty("id").GetString()!;
        await WaitForIndexStatusAsync(projectId, apiKey, databaseId, postsId, indexId, "available");

        var authorId = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Ada" } })).GetProperty("$id").GetString()!;

        await CreateRowAsync(projectId, apiKey, databaseId, postsId, new { data = new { title = "First", authorId } });

        var conflict = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{postsId}/rows", projectId, apiKey: apiKey,
            body: new { data = new { title = "Second", authorId } }));
        // Same 23505-unique-violation catch every other unique index already goes through in
        // RowsService.CreateAsync — no new code path, IndexesService needed no relationship case.
        await AssertError(conflict, 409, ErrorTypes.RowAlreadyExists);
    }

    [Fact]
    public async Task Equal_isNull_and_array_contains_filter_a_relationship_column()
    {
        var (projectId, apiKey, databaseId, authorsId, postsId) = await SetupAsync();
        var a1 = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Ada" } })).GetProperty("$id").GetString()!;
        var a2 = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Grace" } })).GetProperty("$id").GetString()!;

        await CreateRowAsync(projectId, apiKey, databaseId, postsId,
            new { data = new { title = "By Ada", authorId = a1, coAuthorIds = new[] { a2 } } });
        await CreateRowAsync(projectId, apiKey, databaseId, postsId,
            new { data = new { title = "By Grace", authorId = a2 } });

        var equal = await ListAsync(projectId, apiKey, databaseId, postsId,
            $$"""{"method":"equal","attribute":"authorId","values":["{{a1}}"]}""");
        Assert.Equal(1, equal.GetProperty("total").GetInt32());
        Assert.Equal("By Ada", equal.GetProperty("rows")[0].GetProperty("title").GetString());

        // coAuthorIds was never set on the second post -> SQL NULL, not an empty array.
        var isNull = await ListAsync(projectId, apiKey, databaseId, postsId,
            """{"method":"isNull","attribute":"coAuthorIds"}""");
        Assert.Equal(1, isNull.GetProperty("total").GetInt32());
        Assert.Equal("By Grace", isNull.GetProperty("rows")[0].GetProperty("title").GetString());

        var contains = await ListAsync(projectId, apiKey, databaseId, postsId,
            $$"""{"method":"contains","attribute":"coAuthorIds","values":["{{a2}}"]}""");
        Assert.Equal(1, contains.GetProperty("total").GetInt32());
        Assert.Equal("By Ada", contains.GetProperty("rows")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Search_against_a_relationship_column_is_a_clean_400_not_a_silent_wrong_result()
    {
        var (projectId, apiKey, databaseId, _, postsId) = await SetupAsync();

        var query = """{"method":"search","attribute":"authorId","values":["ada"]}""";
        var url = $"/v1/databases/{databaseId}/tables/{postsId}/rows?queries[]={Uri.EscapeDataString(query)}";
        var response = await Client.SendAsync(DataPlane(HttpMethod.Get, url, projectId, apiKey: apiKey));
        await AssertError(response, 400, ErrorTypes.GeneralQueryInvalid);
    }

    // ---- Phase 2: delete-time integrity --------------------------------------------------------

    [Fact]
    public async Task Deleting_an_author_still_referenced_by_a_scalar_relationship_is_rejected()
    {
        var (projectId, apiKey, databaseId, authorsId, postsId) = await SetupAsync();
        var authorId = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Ada" } })).GetProperty("$id").GetString()!;
        await CreateRowAsync(projectId, apiKey, databaseId, postsId,
            new { data = new { title = "Hello", authorId } });

        var response = await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{authorsId}/rows/{authorId}", projectId, apiKey: apiKey));
        // Confirms the new 23503 catch actually fires on the real FK violation — a raw, unhandled
        // 500 before this phase (Phase 1's documented rough edge).
        await AssertError(response, 409, ErrorTypes.RowReferenced);
    }

    [Fact]
    public async Task Deleting_an_author_still_referenced_by_an_array_relationship_is_rejected()
    {
        var (projectId, apiKey, databaseId, authorsId, postsId) = await SetupAsync();
        var a1 = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Ada" } })).GetProperty("$id").GetString()!;
        var a2 = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Grace" } })).GetProperty("$id").GetString()!;
        // a1 is the scalar author; a2 is referenced only through the array column, isolating the
        // array pre-check (there's no FK on this column for Postgres to catch).
        await CreateRowAsync(projectId, apiKey, databaseId, postsId,
            new { data = new { title = "Team post", authorId = a1, coAuthorIds = new[] { a2 } } });

        var response = await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{authorsId}/rows/{a2}", projectId, apiKey: apiKey));
        await AssertError(response, 409, ErrorTypes.RowReferenced);
    }

    [Fact]
    public async Task Deleting_an_author_no_longer_referenced_succeeds()
    {
        var (projectId, apiKey, databaseId, authorsId, _) = await SetupAsync();
        var authorId = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Ada" } })).GetProperty("$id").GetString()!;

        var response = await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{authorsId}/rows/{authorId}", projectId, apiKey: apiKey));
        Assert.Equal(204, (int)response.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_posts_table_is_not_blocked_by_relationship_dependency()
    {
        var (projectId, apiKey, databaseId, _, postsId) = await SetupAsync();

        // posts -> authors is the dependency direction; posts is never anyone's relationship
        // target, so the new gate must not fire for it — only the pre-existing generic gate does.
        var noForce = await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{postsId}", projectId, apiKey: apiKey));
        await AssertError(noForce, 400, ErrorTypes.GeneralForceRequired);

        var withForce = await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{postsId}?force=true", projectId, apiKey: apiKey));
        Assert.Equal(204, (int)withForce.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_authors_table_without_force_is_rejected_with_relationship_dependency()
    {
        var (projectId, apiKey, databaseId, authorsId, _) = await SetupAsync();
        // posts.authorId already targets authors (SetupAsync creates the column); no row needed —
        // this is a pure metadata check.

        var response = await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{authorsId}", projectId, apiKey: apiKey));
        // Confirms the more specific error wins over the generic general_force_required.
        await AssertError(response, 409, ErrorTypes.RelationshipDependency);
    }

    [Fact]
    public async Task Deleting_the_authors_table_with_force_orphans_the_referencing_column()
    {
        var (projectId, apiKey, databaseId, authorsId, postsId) = await SetupAsync();
        var authorId = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Ada" } })).GetProperty("$id").GetString()!;
        var post = await CreateRowAsync(projectId, apiKey, databaseId, postsId,
            new { data = new { title = "Hello", authorId } });
        var postId = post.GetProperty("$id").GetString();

        var response = await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{authorsId}?force=true", projectId, apiKey: apiKey));
        Assert.Equal(204, (int)response.StatusCode);

        // DROP TABLE ... CASCADE silently drops the scalar FK constraint on posts.authorId —
        // the column itself, and its now-dangling value, are orphaned rather than cleaned up.
        var readBack = await Client.SendAsync(DataPlane(HttpMethod.Get,
            $"/v1/databases/{databaseId}/tables/{postsId}/rows/{postId}", projectId, apiKey: apiKey));
        Assert.Equal(200, (int)readBack.StatusCode);
        var body = await ReadJson(readBack);
        Assert.Equal(authorId, body.GetProperty("authorId").GetString());
    }

    [Fact]
    public async Task Writing_to_an_orphaned_relationship_column_after_force_delete_does_not_crash()
    {
        // ColumnDef.TargetTableId is SetNull on the target table's delete (metadata-level, distinct
        // from the physical FK docs/research/table-relationships.md describes) — a subsequent write
        // to the now-target-less column must fall back to accepting the raw id, not throw. Also
        // confirms the referencing table's own CatalogCache entry gets invalidated alongside the
        // deleted table's — otherwise a stale cached TargetTableId still points at a table that no
        // longer exists.
        var (projectId, apiKey, databaseId, authorsId, postsId) = await SetupAsync();
        var authorId = (await CreateRowAsync(projectId, apiKey, databaseId, authorsId,
            new { data = new { name = "Ada" } })).GetProperty("$id").GetString()!;
        var post = await CreateRowAsync(projectId, apiKey, databaseId, postsId,
            new { data = new { title = "Hello", authorId } });
        var postId = post.GetProperty("$id").GetString();

        await Client.SendAsync(DataPlane(HttpMethod.Delete,
            $"/v1/databases/{databaseId}/tables/{authorsId}?force=true", projectId, apiKey: apiKey));

        var newFakeId = Guid.NewGuid().ToString("n");
        var update = await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{postsId}/rows/{postId}", projectId, apiKey: apiKey,
            body: new { data = new { authorId = newFakeId } }));
        Assert.Equal(200, (int)update.StatusCode);
        var updated = await ReadJson(update);
        Assert.Equal(newFakeId, updated.GetProperty("authorId").GetString());
    }

    // ---- setup helpers ------------------------------------------------------------------------

    private async Task<(string ProjectId, string ApiKey, string DatabaseId, string AuthorsId, string PostsId)> SetupAsync()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var (_, apiKey) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read", "databases.write");
        var databaseId = await CreateDatabaseAsync(projectId, apiKey, "blog");

        var authorsId = await CreateTableAsync(projectId, apiKey, databaseId, "authors");
        await CreateColumnAsync(projectId, apiKey, databaseId, authorsId, "string",
            new { key = "name", size = 100, required = true });
        await GrantPublicAsync(projectId, apiKey, databaseId, authorsId);

        var postsId = await CreateTableAsync(projectId, apiKey, databaseId, "posts");
        await CreateColumnAsync(projectId, apiKey, databaseId, postsId, "string",
            new { key = "title", size = 200, required = true });
        var authorColumn = await CreateColumnAsync(projectId, apiKey, databaseId, postsId, "relationship",
            new { key = "authorId", required = true, targetTableId = authorsId });
        Assert.Equal(authorsId, authorColumn.GetProperty("targetTableId").GetString());
        await CreateColumnAsync(projectId, apiKey, databaseId, postsId, "relationship",
            new { key = "coAuthorIds", array = true, targetTableId = authorsId });
        await GrantPublicAsync(projectId, apiKey, databaseId, postsId);

        return (projectId, apiKey, databaseId, authorsId, postsId);
    }

    private async Task<string> CreateDatabaseAsync(string projectId, string apiKey, string key)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post, "/v1/databases", projectId, apiKey: apiKey,
            body: new { key, name = key }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateTableAsync(string projectId, string apiKey, string databaseId, string key)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables", projectId, apiKey: apiKey, body: new { key, name = key }));
        var body = await ReadJson(response);
        Assert.Equal(201, (int)response.StatusCode);
        return body.GetProperty("id").GetString()!;
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

    private async Task GrantPublicAsync(string projectId, string apiKey, string databaseId, string tableId) =>
        await Client.SendAsync(DataPlane(HttpMethod.Patch,
            $"/v1/databases/{databaseId}/tables/{tableId}/permissions", projectId, apiKey: apiKey,
            body: new { permissions = new[] { "create(\"any\")", "read(\"any\")", "update(\"any\")", "delete(\"any\")" } }));

    private async Task<JsonElement> CreateRowAsync(
        string projectId, string apiKey, string databaseId, string tableId, object body)
    {
        var response = await Client.SendAsync(DataPlane(HttpMethod.Post,
            $"/v1/databases/{databaseId}/tables/{tableId}/rows", projectId, apiKey: apiKey, body: body));
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
    }

    private async Task<JsonElement> ListAsync(
        string projectId, string apiKey, string databaseId, string tableId, string query)
    {
        var url = $"/v1/databases/{databaseId}/tables/{tableId}/rows?queries[]={Uri.EscapeDataString(query)}";
        return await ReadJson(await Client.SendAsync(DataPlane(HttpMethod.Get, url, projectId, apiKey: apiKey)));
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
}
