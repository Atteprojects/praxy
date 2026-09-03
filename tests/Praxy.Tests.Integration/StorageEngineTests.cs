using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Storage Phase 1 end-to-end against a real Postgres: the chunk layout, the streaming round trip,
/// bucket permissions through the shared role resolver, the three quotas, and the outbox write.
/// The claims this feature rests on that are invisible from behavior alone — the chunk column's
/// <c>EXTERNAL</c> storage, the exact chunk rows a file produces, that a delete really removes the
/// bytes — are asserted directly against the catalog rather than inferred.
/// </summary>
public class StorageEngineTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    // Small enough that a few-hundred-KB test file spans many chunks without the test writing
    // megabytes; the arithmetic is chunk-size independent (ChunkedWriteStreamTests covers that).
    private const int ChunkSize = 4096;

    protected override IDictionary<string, string?>? ExtraSettings =>
        new Dictionary<string, string?>(base.ExtraSettings!)
        {
            ["Praxy:Storage:ChunkSizeBytes"] = ChunkSize.ToString(),
            ["Praxy:RateLimits:DataPlane:PermitLimit"] = "100000",
        };

    // ---- the storage decision itself ---------------------------------------------------------

    /// <summary>
    /// The migration's <c>SET STORAGE EXTERNAL</c> actually applied. A migration that silently
    /// didn't looks identical from the outside — the only evidence is the catalog
    /// (<c>attstorage</c>: 'e' = external, 'x' = extended, the default this must not be).
    /// </summary>
    [Fact]
    public async Task File_chunk_data_column_uses_external_storage()
    {
        await SetupProjectAsync();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT a.attstorage FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'praxy' AND c.relname = 'file_chunks' AND a.attname = 'data'
            """, conn);
        Assert.Equal('e', (char)(await cmd.ExecuteScalarAsync())!);
    }

    // ---- download is never renderable (found in Phase 1 review) ---------------------------------

    /// <summary>
    /// A file's stored MIME type is whatever the uploader sent, and buckets accept any type by
    /// default — so a download that echoes it back on a renderable document is stored XSS, and the
    /// console is served from this very origin with a SameSite=Lax operator cookie. Every download
    /// must therefore be an attachment with nosniff, whatever the type claims.
    /// </summary>
    [Fact]
    public async Task An_uploaded_html_file_is_served_as_a_non_renderable_attachment()
    {
        var ctx = await BucketWithGrantsAsync();
        var evil = Encoding.UTF8.GetBytes("<script>fetch('/v1/console/projects')</script>");

        var uploaded = await UploadAsync(ctx, "payload.html", evil, "text/html");
        var fileId = uploaded.GetProperty("id").GetString()!;

        var download = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/storage/buckets/{ctx.BucketId}/files/{fileId}/download",
            ctx.ProjectId, sessionToken: ctx.UserToken));

        Assert.Equal(200, (int)download.StatusCode);
        // The type is still reported honestly — it is harmless once the response can't be rendered.
        Assert.Equal("text/html", download.Content.Headers.ContentType?.MediaType);
        // These two are what stop it being rendered.
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("nosniff", download.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(evil, await download.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// The other half: a name carrying CR/LF must never be storable, since it lands in a response
    /// header. ValidateName rejects control characters as a class, not just NUL.
    /// </summary>
    [Theory]
    [InlineData("evil\r\nX-Injected: yes.txt")]
    [InlineData("evil\nSet-Cookie: a=1.txt")]
    [InlineData("evil\u0000.txt")]
    public async Task A_file_name_with_control_characters_is_rejected(string name)
    {
        var ctx = await BucketWithGrantsAsync();
        var response = await UploadResponseAsync(ctx, name, Payload(64));
        Assert.Equal(400, (int)response.StatusCode);
    }

    // ---- round trip ---------------------------------------------------------------------------

    [Fact]
    public async Task A_file_spanning_many_chunks_round_trips_byte_for_byte()
    {
        var ctx = await BucketWithGrantsAsync();
        // 250 KB over a 4 KB chunk: ~62 chunks, and deliberately not a multiple of the chunk size.
        var payload = Payload(256_000 + 17);

        var uploaded = await UploadAsync(ctx, "report.bin", payload, "application/octet-stream");
        var fileId = uploaded.GetProperty("id").GetString()!;

        Assert.Equal(payload.Length, uploaded.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(ChunkSize, uploaded.GetProperty("chunkSizeBytes").GetInt32());
        Assert.Equal(
            (payload.Length + ChunkSize - 1) / ChunkSize, uploaded.GetProperty("chunkCount").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), uploaded.GetProperty("checksum").GetString());

        var download = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/storage/buckets/{ctx.BucketId}/files/{fileId}/download",
            ctx.ProjectId, sessionToken: ctx.UserToken));
        Assert.Equal(200, (int)download.StatusCode);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload.Length, download.Content.Headers.ContentLength);
        Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());

        // The rows are actually what the metadata claims: every chunk but the last is full.
        var lengths = await ChunkLengthsAsync(fileId);
        Assert.Equal(uploaded.GetProperty("chunkCount").GetInt32(), lengths.Count);
        Assert.All(lengths.SkipLast(1), l => Assert.Equal(ChunkSize, l));
        Assert.Equal(payload.Length % ChunkSize, lengths[^1]);
    }

    [Fact]
    public async Task A_file_that_is_an_exact_multiple_of_the_chunk_size_gets_no_empty_trailing_chunk()
    {
        var ctx = await BucketWithGrantsAsync();
        var payload = Payload(ChunkSize * 3);

        var uploaded = await UploadAsync(ctx, "exact.bin", payload);

        Assert.Equal(3, uploaded.GetProperty("chunkCount").GetInt32());
        var lengths = await ChunkLengthsAsync(uploaded.GetProperty("id").GetString()!);
        Assert.Equal([ChunkSize, ChunkSize, ChunkSize], lengths);
    }

    [Fact]
    public async Task A_zero_byte_file_stores_no_chunks_and_downloads_as_empty()
    {
        var ctx = await BucketWithGrantsAsync();

        var uploaded = await UploadAsync(ctx, "empty.txt", [], "text/plain");
        var fileId = uploaded.GetProperty("id").GetString()!;

        Assert.Equal(0, uploaded.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(0, uploaded.GetProperty("chunkCount").GetInt32());
        Assert.Empty(await ChunkLengthsAsync(fileId));

        var download = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/storage/buckets/{ctx.BucketId}/files/{fileId}/download",
            ctx.ProjectId, sessionToken: ctx.UserToken));
        Assert.Equal(200, (int)download.StatusCode);
        Assert.Equal(0, download.Content.Headers.ContentLength);
        Assert.Empty(await download.Content.ReadAsByteArrayAsync());
    }

    // ---- limits --------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_one_byte_over_the_bucket_limit_is_rejected_cleanly_and_stores_nothing()
    {
        var ctx = await BucketWithGrantsAsync(maxFileSizeBytes: 10_000);

        var atLimit = await UploadResponseAsync(ctx, "ok.bin", Payload(10_000));
        Assert.Equal(201, (int)atLimit.StatusCode);

        var over = await UploadResponseAsync(ctx, "too-big.bin", Payload(10_001));
        await AssertError(over, 400, ErrorTypes.FileSizeExceeded);

        // Rolled back whole: the rejected upload left neither a metadata row nor a chunk.
        var files = await ListFilesAsync(ctx);
        Assert.Equal(1, files.GetProperty("total").GetInt32());
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.files WHERE name = 'too-big.bin'"));
        // 10_000 bytes over a 4096-byte chunk is exactly 3 rows — the only chunks in the database,
        // so the rejected upload wrote none of its own before being rolled back.
        Assert.Equal(3L, await ScalarAsync("SELECT count(*) FROM praxy.file_chunks"));
    }

    /// <summary>
    /// The mid-stream case the design doc calls out: a chunked upload declares no
    /// <c>Content-Length</c>, so the up-front check cannot fire and the streaming check has to.
    /// </summary>
    [Fact]
    public async Task An_oversized_upload_with_no_declared_length_is_caught_mid_stream_and_rolled_back()
    {
        var ctx = await BucketWithGrantsAsync(maxFileSizeBytes: 10_000);

        var request = DataPlane(
            HttpMethod.Post, $"/v1/storage/buckets/{ctx.BucketId}/files?name=streamed.bin",
            ctx.ProjectId, sessionToken: ctx.UserToken);
        request.Content = new StreamContent(new MemoryStream(Payload(50_000)));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // Chunked transfer encoding: the server learns the size only by reading it.
        request.Headers.TransferEncodingChunked = true;

        await AssertError(await Client.SendAsync(request), 400, ErrorTypes.FileSizeExceeded);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.files"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.file_chunks"));
    }

    [Fact]
    public async Task The_project_storage_quota_rejects_the_upload_that_would_cross_it()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await SetOrgLimitAsync(projectId, """{"maxStorageBytesPerProject": 20000}""");
        var ctx = await BucketWithGrantsAsync(operatorToken, projectId);

        Assert.Equal(201, (int)(await UploadResponseAsync(ctx, "a.bin", Payload(15_000))).StatusCode);

        var over = await UploadResponseAsync(ctx, "b.bin", Payload(6_000));
        await AssertError(over, 400, ErrorTypes.GeneralResourceLimitExceeded);

        // A file that still fits is accepted, so the quota rejects the byte count and not the call.
        Assert.Equal(201, (int)(await UploadResponseAsync(ctx, "c.bin", Payload(5_000))).StatusCode);
    }

    [Fact]
    public async Task The_bucket_quota_caps_how_many_buckets_a_project_can_have()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await SetOrgLimitAsync(projectId, """{"maxBucketsPerProject": 1}""");

        Assert.Equal(201, (int)(await CreateBucketResponseAsync(operatorToken, projectId, "one")).StatusCode);
        await AssertError(
            await CreateBucketResponseAsync(operatorToken, projectId, "two"),
            400, ErrorTypes.GeneralResourceLimitExceeded);
    }

    /// <summary>A bucket may narrow the resolved per-file quota but never widen it past the instance/org ceiling.</summary>
    [Fact]
    public async Task A_bucket_max_file_size_is_clamped_to_the_quota()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await SetOrgLimitAsync(projectId, """{"maxFileSizeBytes": 4096}""");

        var response = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/storage/buckets", operatorToken,
            new { key = "greedy", name = "Greedy", maxFileSizeBytes = 999_999_999 }));
        var body = await ReadJson(response);

        Assert.Equal(201, (int)response.StatusCode);
        Assert.Equal(4096, body.GetProperty("maxFileSizeBytes").GetInt64());
    }

    [Fact]
    public async Task A_disallowed_mime_type_is_refused_with_its_own_error()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var ctx = await BucketWithGrantsAsync(operatorToken, projectId, allowedMimeTypes: ["image/*"]);

        Assert.Equal(201, (int)(await UploadResponseAsync(ctx, "ok.png", Payload(64), "image/png")).StatusCode);
        await AssertError(
            await UploadResponseAsync(ctx, "no.pdf", Payload(64), "application/pdf"),
            400, ErrorTypes.FileTypeNotAllowed);
    }

    // ---- permissions ---------------------------------------------------------------------------

    [Fact]
    public async Task A_caller_without_the_bucket_grant_is_denied_on_both_read_and_write()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        // Created with no grants at all: deny-by-default, exactly like a new table.
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "locked");
        var (userToken, _) = await SignupAsync(projectId, "nobody@example.com");
        var ctx = new BucketContext(projectId, bucketId, userToken);

        await AssertError(
            await UploadResponseAsync(ctx, "x.bin", Payload(16)), 401, ErrorTypes.GeneralUnauthorized);
        await AssertError(
            await Client.SendAsync(DataPlane(
                HttpMethod.Get, $"/v1/storage/buckets/{bucketId}/files", projectId, sessionToken: userToken)),
            401, ErrorTypes.GeneralUnauthorized);

        // A guest with no grant is denied identically.
        await AssertError(
            await Client.SendAsync(DataPlane(
                HttpMethod.Get, $"/v1/storage/buckets/{bucketId}/files", projectId)),
            401, ErrorTypes.GeneralUnauthorized);
    }

    [Fact]
    public async Task Granting_read_alone_permits_listing_but_not_uploading()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "readonly");
        await SetPermissionsAsync(operatorToken, projectId, bucketId, ["""read("users")"""]);
        var (userToken, _) = await SignupAsync(projectId, "reader@example.com");
        var ctx = new BucketContext(projectId, bucketId, userToken);

        Assert.Equal(0, (await ListFilesAsync(ctx)).GetProperty("total").GetInt32());
        await AssertError(
            await UploadResponseAsync(ctx, "x.bin", Payload(16)), 401, ErrorTypes.GeneralUnauthorized);
    }

    [Fact]
    public async Task A_team_grant_resolves_through_the_shared_role_resolver()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "teamonly");
        var (userToken, _) = await SignupAsync(projectId, "member@example.com");

        var team = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Post, "/v1/teams", projectId, sessionToken: userToken, body: new { name = "Crew" })));
        var teamId = team.GetProperty("id").GetString()!;

        var ctx = new BucketContext(projectId, bucketId, userToken);
        await AssertError(
            await UploadResponseAsync(ctx, "x.bin", Payload(16)), 401, ErrorTypes.GeneralUnauthorized);

        await SetPermissionsAsync(operatorToken, projectId, bucketId,
            [$"""read("team:{teamId}")""", $"""write("team:{teamId}")"""]);

        Assert.Equal(201, (int)(await UploadResponseAsync(ctx, "x.bin", Payload(16))).StatusCode);
    }

    [Fact]
    public async Task An_api_key_needs_the_storage_scope_on_top_of_the_bucket_grant()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "keyed");
        await SetPermissionsAsync(operatorToken, projectId, bucketId, ["""read("any")""", """write("any")"""]);

        var (_, wrongScope) = await CreateApiKeyAsync(operatorToken, projectId, "databases.read");
        await AssertError(
            await Client.SendAsync(DataPlane(
                HttpMethod.Get, $"/v1/storage/buckets/{bucketId}/files", projectId, apiKey: wrongScope)),
            401, ErrorTypes.GeneralUnauthorizedScope);

        var (_, rightScope) = await CreateApiKeyAsync(operatorToken, projectId, "storage.read");
        var allowed = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/storage/buckets/{bucketId}/files", projectId, apiKey: rightScope));
        Assert.Equal(200, (int)allowed.StatusCode);
    }

    // ---- delete --------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_file_removes_its_chunk_rows()
    {
        var ctx = await BucketWithGrantsAsync();
        var uploaded = await UploadAsync(ctx, "doomed.bin", Payload(20_000));
        var fileId = uploaded.GetProperty("id").GetString()!;
        Assert.NotEmpty(await ChunkLengthsAsync(fileId));

        var deleted = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/storage/buckets/{ctx.BucketId}/files/{fileId}",
            ctx.ProjectId, sessionToken: ctx.UserToken));
        Assert.Equal(204, (int)deleted.StatusCode);

        Assert.Empty(await ChunkLengthsAsync(fileId));
        await AssertError(
            await Client.SendAsync(DataPlane(
                HttpMethod.Get, $"/v1/storage/buckets/{ctx.BucketId}/files/{fileId}",
                ctx.ProjectId, sessionToken: ctx.UserToken)),
            404, ErrorTypes.FileNotFound);
    }

    [Fact]
    public async Task Deleting_a_bucket_cascades_to_its_files_and_their_chunks()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var ctx = await BucketWithGrantsAsync(operatorToken, projectId);
        await UploadAsync(ctx, "a.bin", Payload(20_000));
        await UploadAsync(ctx, "b.bin", Payload(9_000));
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM praxy.files"));
        Assert.True((long)(await ScalarAsync("SELECT count(*) FROM praxy.file_chunks"))! > 5);

        // Destructive, so it needs the same force=true confirmation every other destructive delete does.
        await AssertError(
            await Client.SendAsync(Authed(HttpMethod.Delete,
                $"/v1/console/projects/{projectId}/storage/buckets/{ctx.BucketId}", operatorToken)),
            400, ErrorTypes.GeneralForceRequired);

        var forced = await Client.SendAsync(Authed(HttpMethod.Delete,
            $"/v1/console/projects/{projectId}/storage/buckets/{ctx.BucketId}?force=true", operatorToken));
        Assert.Equal(204, (int)forced.StatusCode);

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.files"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.file_chunks"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.bucket_permissions"));
    }

    // ---- outbox --------------------------------------------------------------------------------

    [Fact]
    public async Task Uploading_writes_an_outbox_row_on_the_bucket_file_channel()
    {
        var ctx = await BucketWithGrantsAsync();
        var uploaded = await UploadAsync(ctx, "evented.bin", Payload(64));
        var fileId = uploaded.GetProperty("id").GetString()!;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT type, payload::text FROM praxy.events WHERE type LIKE 'buckets.%' ORDER BY created_at", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal($"buckets.{ctx.BucketId}.files.{fileId}.create", reader.GetString(0));
        var payload = JsonDocument.Parse(reader.GetString(1)).RootElement;
        Assert.Equal(ctx.BucketId, payload.GetProperty("bucketId").GetString());
        Assert.Equal(fileId, payload.GetProperty("fileId").GetString());
        // The read roles travel in the payload, computed pre-commit — a delete has no file left to
        // re-query afterward.
        Assert.Contains("users", payload.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Rename_and_delete_write_their_own_outbox_rows()
    {
        var ctx = await BucketWithGrantsAsync();
        var fileId = (await UploadAsync(ctx, "before.bin", Payload(64))).GetProperty("id").GetString()!;

        var renamed = await Client.SendAsync(DataPlane(
            HttpMethod.Patch, $"/v1/storage/buckets/{ctx.BucketId}/files/{fileId}",
            ctx.ProjectId, sessionToken: ctx.UserToken, body: new { name = "after.bin" }));
        Assert.Equal(200, (int)renamed.StatusCode);
        Assert.Equal("after.bin", (await ReadJson(renamed)).GetProperty("name").GetString());

        Assert.Equal(204, (int)(await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/storage/buckets/{ctx.BucketId}/files/{fileId}",
            ctx.ProjectId, sessionToken: ctx.UserToken))).StatusCode);

        var types = await EventTypesAsync();
        Assert.Equal(
        [
            $"buckets.{ctx.BucketId}.files.{fileId}.create",
            $"buckets.{ctx.BucketId}.files.{fileId}.update",
            $"buckets.{ctx.BucketId}.files.{fileId}.delete",
        ], types);
    }

    // ---- console surface -----------------------------------------------------------------------

    [Fact]
    public async Task The_console_reads_and_writes_files_without_needing_a_bucket_grant()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        // No permissions granted at all — an operator manages the whole project.
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "adminonly");
        var payload = Payload(30_000);

        var upload = await Client.SendAsync(ConsoleUpload(
            operatorToken, projectId, bucketId, "console.bin", payload));
        Assert.Equal(201, (int)upload.StatusCode);
        var fileId = (await ReadJson(upload)).GetProperty("id").GetString()!;

        var download = await Client.SendAsync(Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/storage/buckets/{bucketId}/files/{fileId}/download", operatorToken));
        Assert.Equal(200, (int)download.StatusCode);
        Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());

        var usage = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"/v1/console/projects/{projectId}/storage/usage", operatorToken)));
        Assert.Equal(payload.Length, usage.GetProperty("usedBytes").GetInt64());
    }

    [Fact]
    public async Task Another_projects_bucket_is_not_reachable()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "mine");

        var second = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/console/projects", operatorToken, new { name = "Other" })));
        var otherProjectId = second.GetProperty("id").GetString()!;

        await AssertError(
            await Client.SendAsync(Authed(HttpMethod.Get,
                $"/v1/console/projects/{otherProjectId}/storage/buckets/{bucketId}", operatorToken)),
            404, ErrorTypes.BucketNotFound);
    }

    [Fact]
    public async Task A_duplicate_bucket_key_conflicts()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        await CreateBucketAsync(operatorToken, projectId, "assets");
        await AssertError(
            await CreateBucketResponseAsync(operatorToken, projectId, "assets"),
            409, ErrorTypes.BucketAlreadyExists);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed record BucketContext(string ProjectId, string BucketId, string UserToken);

    private async Task<BucketContext> BucketWithGrantsAsync(long? maxFileSizeBytes = null) =>
        await BucketWithGrantsAsync(await SetupProjectAsync(), maxFileSizeBytes);

    private async Task<BucketContext> BucketWithGrantsAsync(
        (string OperatorToken, string ProjectId) setup, long? maxFileSizeBytes = null) =>
        await BucketWithGrantsAsync(setup.OperatorToken, setup.ProjectId, maxFileSizeBytes);

    private async Task<BucketContext> BucketWithGrantsAsync(
        string operatorToken, string projectId, long? maxFileSizeBytes = null, string[]? allowedMimeTypes = null)
    {
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "assets", maxFileSizeBytes, allowedMimeTypes);
        await SetPermissionsAsync(operatorToken, projectId, bucketId, ["""read("users")""", """write("users")"""]);
        var (userToken, _) = await SignupAsync(projectId, $"user-{Guid.NewGuid():n}@example.com");
        return new BucketContext(projectId, bucketId, userToken);
    }

    private async Task<HttpResponseMessage> CreateBucketResponseAsync(
        string operatorToken, string projectId, string key, long? maxFileSizeBytes = null,
        string[]? allowedMimeTypes = null) =>
        await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/storage/buckets", operatorToken,
            new { key, name = key, maxFileSizeBytes, allowedMimeTypes }));

    private async Task<string> CreateBucketAsync(
        string operatorToken, string projectId, string key, long? maxFileSizeBytes = null,
        string[]? allowedMimeTypes = null)
    {
        var response = await CreateBucketResponseAsync(operatorToken, projectId, key, maxFileSizeBytes, allowedMimeTypes);
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private async Task SetPermissionsAsync(
        string operatorToken, string projectId, string bucketId, string[] permissions)
    {
        var response = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/storage/buckets/{bucketId}/permissions",
            operatorToken, new { permissions }));
        Assert.Equal(200, (int)response.StatusCode);
    }

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static HttpRequestMessage Upload(
        BucketContext ctx, string name, byte[] payload, string contentType)
    {
        var request = DataPlane(
            HttpMethod.Post,
            $"/v1/storage/buckets/{ctx.BucketId}/files?name={Uri.EscapeDataString(name)}",
            ctx.ProjectId, sessionToken: ctx.UserToken);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return request;
    }

    private static HttpRequestMessage ConsoleUpload(
        string operatorToken, string projectId, string bucketId, string name, byte[] payload)
    {
        var request = Authed(
            HttpMethod.Post,
            $"/v1/console/projects/{projectId}/storage/buckets/{bucketId}/files?name={Uri.EscapeDataString(name)}",
            operatorToken);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    private Task<HttpResponseMessage> UploadResponseAsync(
        BucketContext ctx, string name, byte[] payload, string contentType = "application/octet-stream") =>
        Client.SendAsync(Upload(ctx, name, payload, contentType));

    private async Task<JsonElement> UploadAsync(
        BucketContext ctx, string name, byte[] payload, string contentType = "application/octet-stream")
    {
        var response = await UploadResponseAsync(ctx, name, payload, contentType);
        Assert.Equal(201, (int)response.StatusCode);
        return await ReadJson(response);
    }

    private async Task<JsonElement> ListFilesAsync(BucketContext ctx)
    {
        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/storage/buckets/{ctx.BucketId}/files", ctx.ProjectId, sessionToken: ctx.UserToken));
        Assert.Equal(200, (int)response.StatusCode);
        return await ReadJson(response);
    }

    private async Task<List<int>> ChunkLengthsAsync(string fileId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """SELECT octet_length(data) FROM praxy.file_chunks WHERE file_id = $1 ORDER BY "index" """, conn);
        cmd.Parameters.AddWithValue(Guid.Parse(fileId));
        await using var reader = await cmd.ExecuteReaderAsync();
        var lengths = new List<int>();
        while (await reader.ReadAsync())
            lengths.Add(reader.GetInt32(0));
        return lengths;
    }

    private async Task<List<string>> EventTypesAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT type FROM praxy.events WHERE type LIKE 'buckets.%' ORDER BY created_at, id", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var types = new List<string>();
        while (await reader.ReadAsync())
            types.Add(reader.GetString(0));
        return types;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task SetOrgLimitAsync(string projectId, string limitsJson)
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
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }
}
