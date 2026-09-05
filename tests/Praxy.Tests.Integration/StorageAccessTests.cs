using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// Storage Phase 2 end-to-end against a real Postgres: per-file permissions (and specifically their
/// <b>additive</b> nature), HTTP Range, and opt-in inline serving.
///
/// The listing assertions check <c>total</c> as well as the rows on purpose — that is where an
/// in-memory filter applied after pagination shows up, and it is the one bug in this area that
/// looks fine in a two-file test that only reads the array.
/// </summary>
public class StorageAccessTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    // Small enough that a modest test payload spans many chunks, so ranges land mid-chunk.
    private const int ChunkSize = 1024;

    protected override IDictionary<string, string?>? ExtraSettings =>
        new Dictionary<string, string?>(base.ExtraSettings!)
        {
            ["Praxy:Storage:ChunkSizeBytes"] = ChunkSize.ToString(),
            ["Praxy:RateLimits:DataPlane:PermitLimit"] = "100000",
        };

    // ---- per-file permissions ------------------------------------------------------------------

    /// <summary>
    /// The headline use case: "users can only read their own uploads", which is configured by
    /// granting <i>no</i> bucket-level read at all, turning file_security on, and attaching a
    /// per-file grant. Each user must see exactly their own file — in <c>get</c>, in
    /// <c>download</c>, and in the <c>list</c> total.
    /// </summary>
    [Fact]
    public async Task A_per_file_grant_reaches_that_file_and_nothing_else()
    {
        var ctx = await OwnerOnlyBucketAsync();

        var alice = await UploadOwnedAsync(ctx, ctx.Alice, "alice.txt");
        var bob = await UploadOwnedAsync(ctx, ctx.Bob, "bob.txt");

        // Each sees one file, and the count agrees with the rows — the post-pagination-filter test.
        var aliceList = await ListAsync(ctx, ctx.Alice.Token);
        Assert.Equal(1, aliceList.GetProperty("total").GetInt32());
        Assert.Equal(["alice.txt"], Names(aliceList));

        var bobList = await ListAsync(ctx, ctx.Bob.Token);
        Assert.Equal(1, bobList.GetProperty("total").GetInt32());
        Assert.Equal(["bob.txt"], Names(bobList));

        // Alice reads her own file and its bytes…
        Assert.Equal(200, (int)(await GetFileAsync(ctx, ctx.Alice.Token, alice)).StatusCode);
        Assert.Equal(200, (int)(await DownloadAsync(ctx, ctx.Alice.Token, alice)).StatusCode);

        // …and Bob's is a 404, not a 401: the same answer a row she can't see gives, so the status
        // code doesn't leak that someone else's file exists.
        await AssertError(await GetFileAsync(ctx, ctx.Alice.Token, bob), 404, ErrorTypes.FileNotFound);
        await AssertError(await DownloadAsync(ctx, ctx.Alice.Token, bob), 404, ErrorTypes.FileNotFound);

        // The grant is per action, too: read does not imply delete.
        await AssertError(
            await Client.SendAsync(DataPlane(HttpMethod.Delete, FilePath(ctx, alice), ctx.ProjectId,
                sessionToken: ctx.Alice.Token)),
            404, ErrorTypes.FileNotFound);
    }

    /// <summary>
    /// The property the whole design hangs off: per-file grants are <b>additive</b>. A bucket-level
    /// read reaches every file and nothing attached to a file can claw it back — exactly how a
    /// table-level grant overrides row security. If this ever fails, someone has built a second
    /// authorization model.
    /// </summary>
    [Fact]
    public async Task A_bucket_read_grant_makes_every_file_visible_regardless_of_per_file_grants()
    {
        var ctx = await OwnerOnlyBucketAsync();
        await UploadOwnedAsync(ctx, ctx.Alice, "alice.txt");
        await UploadOwnedAsync(ctx, ctx.Bob, "bob.txt");

        Assert.Equal(1, (await ListAsync(ctx, ctx.Alice.Token)).GetProperty("total").GetInt32());

        // The per-file rows stay exactly as they were; only the bucket matrix widens.
        await SetPermissionsAsync(ctx, ["""create("users")""", """read("any")"""]);

        foreach (var token in new[] { ctx.Alice.Token, ctx.Bob.Token })
        {
            var list = await ListAsync(ctx, token);
            Assert.Equal(2, list.GetProperty("total").GetInt32());
            Assert.Equal(["alice.txt", "bob.txt"], Names(list).Order());
        }

        // A guest with no session reads them too — read("any") means any.
        var guest = await ListAsync(ctx, sessionToken: null);
        Assert.Equal(2, guest.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// No auto-grant on upload: rows don't get one either, and a magic grant would be a rule that
    /// only some callers want. An upload with no <c>permissions</c> is unreachable by its own
    /// uploader in an owner-only bucket, which is surprising exactly once and then explicit forever.
    /// </summary>
    [Fact]
    public async Task An_upload_with_no_permissions_grants_nobody_anything()
    {
        var ctx = await OwnerOnlyBucketAsync();
        var fileId = await UploadAsync(ctx, ctx.Alice.Token, "orphan.txt", permissions: null);

        Assert.Equal(0, (await ListAsync(ctx, ctx.Alice.Token)).GetProperty("total").GetInt32());
        await AssertError(await GetFileAsync(ctx, ctx.Alice.Token, fileId), 404, ErrorTypes.FileNotFound);

        // The console still sees it — an operator is above this model entirely.
        var console = await ReadJson(await Client.SendAsync(Authed(
            HttpMethod.Get, $"{ConsoleBase(ctx)}/buckets/{ctx.BucketId}/files", ctx.OperatorToken)));
        Assert.Equal(1, console.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// With file_security off, the bucket matrix is the only level — so per-file grants would be
    /// dead configuration that reads as protection. Refused loudly instead.
    /// </summary>
    [Fact]
    public async Task Per_file_permissions_are_refused_on_a_bucket_that_does_not_consult_them()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "plain", fileSecurity: false);
        await SetPermissionsAsync(operatorToken, projectId, bucketId, ["""create("users")""", """read("users")"""]);
        var (token, user) = await SignupAsync(projectId, $"u-{Guid.NewGuid():n}@example.com");

        var response = await Client.SendAsync(UploadRequest(
            projectId, bucketId, token, "x.txt", "text/plain", Payload(32),
            [$"""read("user:{user.GetProperty("id").GetString()}")"""]));
        await AssertError(response, 400, ErrorTypes.GeneralArgumentInvalid);

        // And $permissions reads back empty on such a bucket rather than reporting rows nothing consults.
        var uploaded = await Client.SendAsync(UploadRequest(
            projectId, bucketId, token, "y.txt", "text/plain", Payload(32), permissions: null));
        Assert.Equal(201, (int)uploaded.StatusCode);
        Assert.Empty((await ReadJson(uploaded)).GetProperty("$permissions").EnumerateArray());
    }

    /// <summary>
    /// <c>create</c> is bucket-level only: there is no file yet for a grant to hang off, so
    /// <c>write(...)</c> — which expands to include create — is refused rather than silently
    /// storing a grant that can never be consulted. Same rule rows have.
    /// </summary>
    [Fact]
    public async Task A_file_cannot_be_granted_create_or_write()
    {
        var ctx = await OwnerOnlyBucketAsync();
        foreach (var permission in new[] { """create("users")""", """write("users")""" })
        {
            await AssertError(
                await Client.SendAsync(UploadRequest(
                    ctx.ProjectId, ctx.BucketId, ctx.Alice.Token, "x.txt", "text/plain", Payload(32),
                    [permission])),
                400, ErrorTypes.GeneralArgumentInvalid);
        }
    }

    /// <summary>The permissions endpoint is gated on <c>update</c> for the file, so an owner can re-share their own upload.</summary>
    [Fact]
    public async Task A_files_own_grants_can_be_read_and_replaced_through_its_permissions_endpoint()
    {
        var ctx = await OwnerOnlyBucketAsync();
        var fileId = await UploadAsync(ctx, ctx.Alice.Token, "shared.txt",
            [Read(ctx.Alice), Update(ctx.Alice)]);

        // The dedicated endpoint answers with a plain `permissions` list, like the bucket one; the
        // `$permissions` spelling is the file *document*'s field, mirroring a row's.
        var current = await ReadJson(await Client.SendAsync(DataPlane(
            HttpMethod.Get, $"{FilePath(ctx, fileId)}/permissions", ctx.ProjectId, sessionToken: ctx.Alice.Token)));
        Assert.Equal([Read(ctx.Alice), Update(ctx.Alice)], Listed(current, "permissions").Order());
        Assert.Equal([Read(ctx.Alice), Update(ctx.Alice)],
            Permissions(await ReadJson(await GetFileAsync(ctx, ctx.Alice.Token, fileId))).Order());

        // Bob can't see it yet…
        await AssertError(await GetFileAsync(ctx, ctx.Bob.Token, fileId), 404, ErrorTypes.FileNotFound);

        // …and can't grant himself access either: he has no update grant on it.
        await AssertError(
            await Client.SendAsync(DataPlane(
                HttpMethod.Patch, $"{FilePath(ctx, fileId)}/permissions", ctx.ProjectId,
                sessionToken: ctx.Bob.Token, body: new { permissions = new[] { Read(ctx.Bob) } })),
            404, ErrorTypes.FileNotFound);

        // Alice, who does, shares it with him.
        var replaced = await Client.SendAsync(DataPlane(
            HttpMethod.Patch, $"{FilePath(ctx, fileId)}/permissions", ctx.ProjectId,
            sessionToken: ctx.Alice.Token,
            body: new { permissions = new[] { Read(ctx.Alice), Update(ctx.Alice), Read(ctx.Bob) } }));
        Assert.Equal(200, (int)replaced.StatusCode);

        Assert.Equal(200, (int)(await GetFileAsync(ctx, ctx.Bob.Token, fileId)).StatusCode);
        Assert.Equal(1, (await ListAsync(ctx, ctx.Bob.Token)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Deleting_a_file_cascades_its_permission_rows()
    {
        var ctx = await OwnerOnlyBucketAsync();
        var fileId = await UploadAsync(ctx, ctx.Alice.Token, "doomed.txt",
            [Read(ctx.Alice), Delete(ctx.Alice)]);
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM praxy.file_permissions"));

        var deleted = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, FilePath(ctx, fileId), ctx.ProjectId, sessionToken: ctx.Alice.Token));
        Assert.Equal(204, (int)deleted.StatusCode);

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.file_permissions"));
    }

    // ---- HTTP Range ----------------------------------------------------------------------------

    /// <summary>
    /// A 206 must return exactly the requested bytes — compared here against the same slice of a
    /// full download, so an off-by-one at either end fails rather than looking plausible. The
    /// payload spans several chunks and the ranges deliberately start and end mid-chunk.
    /// </summary>
    [Fact]
    public async Task A_partial_request_returns_exactly_the_requested_slice()
    {
        var ctx = await OpenBucketAsync();
        var payload = Payload(ChunkSize * 4 + 137);
        var fileId = await UploadAsync(ctx, ctx.Alice.Token, "clip.bin", null, payload, "application/octet-stream");

        var full = await DownloadAsync(ctx, ctx.Alice.Token, fileId);
        Assert.Equal(200, (int)full.StatusCode);
        Assert.Equal(payload, await full.Content.ReadAsByteArrayAsync());
        // Advertised on the full response, which is how a player learns it can seek at all.
        Assert.Contains("bytes", full.Headers.AcceptRanges);

        foreach (var (header, start, end) in new (string, int, int)[]
        {
            ("bytes=0-99", 0, 99),                                             // from the very start
            ("bytes=1500-2600", 1500, 2600),                                   // starts and ends mid-chunk
            ($"bytes={ChunkSize}-{ChunkSize * 2 - 1}", ChunkSize, ChunkSize * 2 - 1), // exactly one chunk
            ($"bytes=3000-", 3000, payload.Length - 1),                        // open-ended
            ("bytes=-100", payload.Length - 100, payload.Length - 1),          // suffix
            ($"bytes=0-{payload.Length - 1}", 0, payload.Length - 1),          // the whole file, as a range
        })
        {
            var response = await DownloadAsync(ctx, ctx.Alice.Token, fileId, header);
            Assert.Equal(206, (int)response.StatusCode);
            Assert.Equal($"bytes {start}-{end}/{payload.Length}",
                response.Content.Headers.ContentRange?.ToString());
            // The length of the *part*, not of the file: getting this wrong makes players hang.
            Assert.Equal(end - start + 1, response.Content.Headers.ContentLength);
            Assert.Equal(payload[start..(end + 1)], await response.Content.ReadAsByteArrayAsync());
            // A 206 is still an attachment — Range and Content-Disposition are orthogonal.
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        }
    }

    [Fact]
    public async Task A_range_past_the_end_is_a_416_that_reports_the_real_size()
    {
        var ctx = await OpenBucketAsync();
        var payload = Payload(500);
        var fileId = await UploadAsync(ctx, ctx.Alice.Token, "short.bin", null, payload, "application/octet-stream");

        var response = await DownloadAsync(ctx, ctx.Alice.Token, fileId, "bytes=500-999");
        await AssertError(response, 416, ErrorTypes.FileRangeNotSatisfiable);
        // "*/total" is what lets the client fix its offset and retry.
        Assert.Equal("bytes */500", response.Content.Headers.ContentRange?.ToString());
    }

    /// <summary>
    /// Multi-range is answered with the whole file rather than <c>multipart/byteranges</c>: the spec
    /// permits ignoring a Range header, and no browser needs multipart for media playback.
    /// </summary>
    [Fact]
    public async Task A_multi_range_request_gets_the_whole_file_with_a_200()
    {
        var ctx = await OpenBucketAsync();
        var payload = Payload(3000);
        var fileId = await UploadAsync(ctx, ctx.Alice.Token, "multi.bin", null, payload, "application/octet-stream");

        var response = await DownloadAsync(ctx, ctx.Alice.Token, fileId, "bytes=0-99,200-299");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>The console download is the same code path, and an operator seeking a video should get a 206 too.</summary>
    [Fact]
    public async Task The_console_download_honours_ranges_as_well()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "adminonly");
        var payload = Payload(2500);

        var upload = await Client.SendAsync(ConsoleUpload(operatorToken, projectId, bucketId, "v.bin", payload));
        Assert.Equal(201, (int)upload.StatusCode);
        var fileId = (await ReadJson(upload)).GetProperty("id").GetString()!;

        var request = Authed(HttpMethod.Get,
            $"/v1/console/projects/{projectId}/storage/buckets/{bucketId}/files/{fileId}/download", operatorToken);
        request.Headers.Range = RangeHeaderValue.Parse("bytes=1000-1999");
        var response = await Client.SendAsync(request);

        Assert.Equal(206, (int)response.StatusCode);
        Assert.Equal(payload[1000..2000], await response.Content.ReadAsByteArrayAsync());
    }

    // ---- inline serving ------------------------------------------------------------------------

    /// <summary>
    /// Opting a type in is per bucket and intersected with a hard-coded safe set. A bucket that
    /// serves PNGs inline still serves <c>text/html</c> as an attachment — that is the whole
    /// control, since a file's stored MIME type is whatever the uploader sent.
    /// </summary>
    [Fact]
    public async Task An_allowlisted_type_serves_inline_while_html_in_the_same_bucket_stays_an_attachment()
    {
        var ctx = await OpenBucketAsync(inlineTypes: ["image/png"]);

        var png = await UploadAsync(ctx, ctx.Alice.Token, "cat.png", null, Payload(64), "image/png");
        var html = await UploadAsync(ctx, ctx.Alice.Token, "payload.html", null,
            Encoding.UTF8.GetBytes("<script>fetch('/v1/console/projects')</script>"), "text/html");
        var pdf = await UploadAsync(ctx, ctx.Alice.Token, "doc.pdf", null, Payload(64), "application/pdf");

        var inline = await DownloadAsync(ctx, ctx.Alice.Token, png);
        Assert.Equal("inline", inline.Content.Headers.ContentDisposition?.DispositionType);
        // nosniff stays on even for the inline case — that is not what inline opts out of.
        Assert.Contains("nosniff", inline.Headers.GetValues("X-Content-Type-Options"));

        var attachment = await DownloadAsync(ctx, ctx.Alice.Token, html);
        Assert.Equal("attachment", attachment.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("nosniff", attachment.Headers.GetValues("X-Content-Type-Options"));

        // A safe type this bucket didn't opt in is still an attachment: both gates, not either.
        var notOptedIn = await DownloadAsync(ctx, ctx.Alice.Token, pdf);
        Assert.Equal("attachment", notOptedIn.Content.Headers.ContentDisposition?.DispositionType);
    }

    /// <summary>
    /// <c>text/html</c> and <c>image/svg+xml</c> can't even be *stored* in the allow-list, so a
    /// bucket can never carry configuration that reads as "render my HTML" and is silently ignored.
    /// </summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("image/*")]
    public async Task An_unsafe_inline_type_is_rejected_when_the_bucket_is_configured(string type)
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var response = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/storage/buckets", operatorToken,
            new { key = "risky", name = "risky", inlineTypes = new[] { type } }));
        await AssertError(response, 400, ErrorTypes.GeneralArgumentInvalid);
    }

    [Fact]
    public async Task A_partial_response_of_an_inline_type_stays_inline()
    {
        var ctx = await OpenBucketAsync(inlineTypes: ["video/mp4"]);
        var payload = Payload(4000);
        var fileId = await UploadAsync(ctx, ctx.Alice.Token, "clip.mp4", null, payload, "video/mp4");

        var response = await DownloadAsync(ctx, ctx.Alice.Token, fileId, "bytes=100-199");
        Assert.Equal(206, (int)response.StatusCode);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(payload[100..200], await response.Content.ReadAsByteArrayAsync());
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed record TestUser(string Token, string Id);

    private sealed record Ctx(string OperatorToken, string ProjectId, string BucketId, TestUser Alice, TestUser Bob);

    private static string Read(TestUser user) => $"""read("user:{user.Id}")""";

    private static string Update(TestUser user) => $"""update("user:{user.Id}")""";

    private static string Delete(TestUser user) => $"""delete("user:{user.Id}")""";

    /// <summary>The "users see only their own uploads" configuration: create at bucket level, read nowhere.</summary>
    private async Task<Ctx> OwnerOnlyBucketAsync()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "uploads", fileSecurity: true);
        await SetPermissionsAsync(operatorToken, projectId, bucketId, ["""create("users")"""]);
        return new Ctx(operatorToken, projectId, bucketId,
            await UserAsync(projectId), await UserAsync(projectId));
    }

    /// <summary>A plain bucket with full access for signed-in users — the Range/inline cases don't need per-file grants.</summary>
    private async Task<Ctx> OpenBucketAsync(string[]? inlineTypes = null)
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "assets", inlineTypes: inlineTypes);
        await SetPermissionsAsync(operatorToken, projectId, bucketId, ["""read("users")""", """write("users")"""]);
        return new Ctx(operatorToken, projectId, bucketId,
            await UserAsync(projectId), await UserAsync(projectId));
    }

    private async Task<TestUser> UserAsync(string projectId)
    {
        var (token, user) = await SignupAsync(projectId, $"u-{Guid.NewGuid():n}@example.com");
        return new TestUser(token, user.GetProperty("id").GetString()!);
    }

    private static string ConsoleBase(Ctx ctx) => $"/v1/console/projects/{ctx.ProjectId}/storage";

    private static string FilesPath(Ctx ctx) => $"/v1/storage/buckets/{ctx.BucketId}/files";

    private static string FilePath(Ctx ctx, string fileId) => $"{FilesPath(ctx)}/{fileId}";

    private async Task<string> CreateBucketAsync(
        string operatorToken, string projectId, string key, bool? fileSecurity = null,
        string[]? inlineTypes = null)
    {
        var response = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/storage/buckets", operatorToken,
            new { key, name = key, fileSecurity, inlineTypes }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private Task SetPermissionsAsync(Ctx ctx, string[] permissions) =>
        SetPermissionsAsync(ctx.OperatorToken, ctx.ProjectId, ctx.BucketId, permissions);

    private async Task SetPermissionsAsync(
        string operatorToken, string projectId, string bucketId, string[] permissions)
    {
        var response = await Client.SendAsync(Authed(
            HttpMethod.Patch, $"/v1/console/projects/{projectId}/storage/buckets/{bucketId}/permissions",
            operatorToken, new { permissions }));
        Assert.Equal(200, (int)response.StatusCode);
    }

    private static HttpRequestMessage UploadRequest(
        string projectId, string bucketId, string sessionToken, string name, string contentType,
        byte[] payload, string[]? permissions)
    {
        var query = $"?name={Uri.EscapeDataString(name)}";
        foreach (var permission in permissions ?? [])
            query += $"&permissions={Uri.EscapeDataString(permission)}";
        var request = DataPlane(
            HttpMethod.Post, $"/v1/storage/buckets/{bucketId}/files{query}", projectId, sessionToken: sessionToken);
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

    /// <summary>Uploads a file granting its uploader read+update+delete — the "my own upload" shape.</summary>
    private Task<string> UploadOwnedAsync(Ctx ctx, TestUser user, string name) =>
        UploadAsync(ctx, user.Token, name, [Read(user), Update(user)]);

    private async Task<string> UploadAsync(
        Ctx ctx, string sessionToken, string name, string[]? permissions,
        byte[]? payload = null, string contentType = "text/plain")
    {
        var response = await Client.SendAsync(UploadRequest(
            ctx.ProjectId, ctx.BucketId, sessionToken, name, contentType, payload ?? Payload(64), permissions));
        Assert.Equal(201, (int)response.StatusCode);
        var body = await ReadJson(response);
        // The upload echoes back what it stored, under the same $permissions name a row uses.
        Assert.Equal((permissions ?? []).Order(), Permissions(body).Order());
        return body.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> ListAsync(Ctx ctx, string? sessionToken)
    {
        var response = await Client.SendAsync(DataPlane(
            HttpMethod.Get, FilesPath(ctx), ctx.ProjectId, sessionToken: sessionToken));
        Assert.Equal(200, (int)response.StatusCode);
        return await ReadJson(response);
    }

    private Task<HttpResponseMessage> GetFileAsync(Ctx ctx, string sessionToken, string fileId) =>
        Client.SendAsync(DataPlane(HttpMethod.Get, FilePath(ctx, fileId), ctx.ProjectId, sessionToken: sessionToken));

    private Task<HttpResponseMessage> DownloadAsync(
        Ctx ctx, string sessionToken, string fileId, string? range = null)
    {
        var request = DataPlane(
            HttpMethod.Get, $"{FilePath(ctx, fileId)}/download", ctx.ProjectId, sessionToken: sessionToken);
        if (range is not null)
            request.Headers.TryAddWithoutValidation("Range", range);
        return Client.SendAsync(request);
    }

    private static string[] Names(JsonElement list) =>
        [.. list.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)];

    private static string[] Permissions(JsonElement file) => Listed(file, "$permissions");

    private static string[] Listed(JsonElement body, string property) =>
        [.. body.GetProperty(property).EnumerateArray().Select(p => p.GetString()!)];

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }
}
