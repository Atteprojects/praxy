using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;
using SkiaSharp;

namespace Praxy.Tests.Integration;

/// <summary>
/// Storage Phase 3 end-to-end against a real Postgres: a transform produces a real cached
/// derivative, the cache actually caches (asserted via row count, not timing), permissions inherit
/// from the source file with no second check, deletion/re-upload invalidation, the ladder's 400 on
/// an out-of-range request, and the inline-serving default is unchanged for a derivative's own type.
/// </summary>
public class StorageDerivativesTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    protected override IDictionary<string, string?>? ExtraSettings =>
        new Dictionary<string, string?>(base.ExtraSettings!)
        {
            ["Praxy:RateLimits:DataPlane:PermitLimit"] = "100000",
        };

    [Fact]
    public async Task A_transform_produces_the_requested_dimensions_and_a_stable_checksum_on_repeat()
    {
        var ctx = await BucketWithGrantsAsync();
        var fileId = await UploadImageAsync(ctx, 400, 300);

        var first = await DownloadAsync(ctx, fileId, "?width=100");
        Assert.Equal(200, (int)first.StatusCode);
        var firstBytes = await first.Content.ReadAsByteArrayAsync();
        AssertPixelSize(firstBytes, 128, 96); // 100 snaps to 128; height derives from the 4:3 source

        var second = await DownloadAsync(ctx, fileId, "?width=100");
        var secondBytes = await second.Content.ReadAsByteArrayAsync();
        Assert.Equal(firstBytes, secondBytes);
    }

    /// <summary>Asserted via the derivative row count, per the prompt's own instruction — timing is not a reliable cache-hit signal.</summary>
    [Fact]
    public async Task A_second_request_for_the_same_transform_is_served_from_cache_not_regenerated()
    {
        var ctx = await BucketWithGrantsAsync();
        var fileId = await UploadImageAsync(ctx, 400, 300);

        Assert.Equal(200, (int)(await DownloadAsync(ctx, fileId, "?width=100")).StatusCode);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivatives"));

        Assert.Equal(200, (int)(await DownloadAsync(ctx, fileId, "?width=100")).StatusCode);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivatives"));

        // A different key is a genuinely new row, not a coincidence of the count staying at 1.
        Assert.Equal(200, (int)(await DownloadAsync(ctx, fileId, "?width=300")).StatusCode);
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivatives"));
    }

    [Fact]
    public async Task A_caller_who_cannot_read_the_source_cannot_read_a_derivative_of_it()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        // No grants at all: deny-by-default reaches the transform path exactly like the plain one.
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "locked");
        var fileId = await UploadImageAsync(
            new BucketContext(projectId, bucketId, ""), 200, 200, operatorToken, asOperator: true);
        var (userToken, _) = await SignupAsync(projectId, "nobody@example.com");

        await AssertError(
            await Client.SendAsync(DataPlane(
                HttpMethod.Get, $"/v1/storage/buckets/{bucketId}/files/{fileId}/download?width=100",
                projectId, sessionToken: userToken)),
            401, ErrorTypes.GeneralUnauthorized);

        // No derivative was ever generated for a caller who was never allowed to see the source.
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivatives"));
    }

    [Fact]
    public async Task Deleting_the_source_cascades_its_derivatives()
    {
        var ctx = await BucketWithGrantsAsync();
        var fileId = await UploadImageAsync(ctx, 200, 200);
        Assert.Equal(200, (int)(await DownloadAsync(ctx, fileId, "?width=100")).StatusCode);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivatives"));

        var deleted = await Client.SendAsync(DataPlane(
            HttpMethod.Delete, $"/v1/storage/buckets/{ctx.BucketId}/files/{fileId}",
            ctx.ProjectId, sessionToken: ctx.UserToken));
        Assert.Equal(204, (int)deleted.StatusCode);

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivatives"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivative_chunks"));
    }

    [Fact]
    public async Task Re_uploading_over_the_file_id_purges_its_derivatives()
    {
        var ctx = await BucketWithGrantsAsync();
        var fileId = await UploadImageAsync(ctx, 200, 200);
        Assert.Equal(200, (int)(await DownloadAsync(ctx, fileId, "?width=100")).StatusCode);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivatives"));

        var replacement = EncodePng(200, 200, SKColors.Firebrick);
        var replace = DataPlane(
            HttpMethod.Put, $"/v1/storage/buckets/{ctx.BucketId}/files/{fileId}",
            ctx.ProjectId, sessionToken: ctx.UserToken);
        replace.Content = new ByteArrayContent(replacement);
        replace.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        Assert.Equal(200, (int)(await Client.SendAsync(replace)).StatusCode);

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivatives"));

        // The old thumbnail is gone, not stale: a fresh request for the same key re-generates
        // against the *new* bytes rather than quietly serving what the deleted row would have.
        Assert.Equal(200, (int)(await DownloadAsync(ctx, fileId, "?width=100")).StatusCode);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivatives"));
    }

    [Fact]
    public async Task A_bucket_that_has_not_opted_the_output_type_into_inline_still_gets_attachment()
    {
        var ctx = await BucketWithGrantsAsync();
        var fileId = await UploadImageAsync(ctx, 200, 200);

        var response = await DownloadAsync(ctx, fileId, "?width=100&format=jpeg");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task A_request_above_the_top_rung_is_a_clean_400()
    {
        var ctx = await BucketWithGrantsAsync();
        var fileId = await UploadImageAsync(ctx, 200, 200);

        await AssertError(await DownloadAsync(ctx, fileId, "?width=2049"), 400, ErrorTypes.FileTransformInvalid);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM praxy.file_derivatives"));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed record BucketContext(string ProjectId, string BucketId, string UserToken);

    private async Task<BucketContext> BucketWithGrantsAsync()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId, "assets");
        await SetPermissionsAsync(operatorToken, projectId, bucketId, ["""read("users")""", """write("users")"""]);
        var (userToken, _) = await SignupAsync(projectId, $"user-{Guid.NewGuid():n}@example.com");
        return new BucketContext(projectId, bucketId, userToken);
    }

    private async Task<string> CreateBucketAsync(string operatorToken, string projectId, string key)
    {
        var response = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/storage/buckets", operatorToken,
            new { key, name = key }));
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

    private static byte[] EncodePng(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void AssertPixelSize(byte[] encoded, int width, int height)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data);
        Assert.Equal(width, codec!.Info.Width);
        Assert.Equal(height, codec.Info.Height);
    }

    private async Task<string> UploadImageAsync(
        BucketContext ctx, int width, int height, string? operatorToken = null, bool asOperator = false)
    {
        var payload = EncodePng(width, height, SKColors.CornflowerBlue);
        var request = asOperator
            ? Authed(
                HttpMethod.Post,
                $"/v1/console/projects/{ctx.ProjectId}/storage/buckets/{ctx.BucketId}/files?name=photo.png",
                operatorToken!)
            : DataPlane(
                HttpMethod.Post, $"/v1/storage/buckets/{ctx.BucketId}/files?name=photo.png",
                ctx.ProjectId, sessionToken: ctx.UserToken);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        var response = await Client.SendAsync(request);
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> DownloadAsync(BucketContext ctx, string fileId, string query) =>
        Client.SendAsync(DataPlane(
            HttpMethod.Get, $"/v1/storage/buckets/{ctx.BucketId}/files/{fileId}/download{query}",
            ctx.ProjectId, sessionToken: ctx.UserToken));

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }
}
