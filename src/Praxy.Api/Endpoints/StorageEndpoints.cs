using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Storage;
using Praxy.Tables.Quotas;

namespace Praxy.Api.Endpoints;

/// <summary>
/// The data plane for files (<c>/v1/storage</c>). File operations are reachable by app-user
/// sessions, guests and API keys alike — bucket permissions do the access control, exactly as
/// table/row permissions do for <see cref="RowEndpoints"/>; a key additionally needs the matching
/// <c>storage.read</c>/<c>storage.write</c> scope, a session or guest does not.
///
/// Bucket management (create/update/delete and the permission matrix) is the storage analogue of
/// <see cref="DatabaseEndpoints"/>'s schema surface: API-key callers only, scoped, because
/// configuring a bucket is a server/CI concern rather than something an end-user session does.
/// </summary>
public static class StorageEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/v1/storage")
            .AddEndpointFilter<DataPlaneEndpoints.ProjectGuardFilter>()
            .AddEndpointFilter<AppPrincipalFilter>()
            .RequireRateLimiting("data-plane");

        // ---- buckets: key-scoped management ----
        group.MapPost("/buckets", CreateBucket).Produces<BucketResponse>(StatusCodes.Status201Created);
        group.MapGet("/buckets", ListBuckets).Produces<BucketListResponse>();
        group.MapGet("/buckets/{bucketId}", GetBucket).Produces<BucketResponse>();
        group.MapPatch("/buckets/{bucketId}", UpdateBucket).Produces<BucketResponse>();
        group.MapDelete("/buckets/{bucketId}", DeleteBucket).Produces(StatusCodes.Status204NoContent);

        group.MapGet("/buckets/{bucketId}/permissions", GetPermissions).Produces<BucketPermissionsResponse>();
        group.MapPatch("/buckets/{bucketId}/permissions", UpdatePermissions).Produces<BucketPermissionsResponse>();

        // ---- files: permission-gated, open to sessions and guests ----
        group.MapPost("/buckets/{bucketId}/files", CreateFile)
            .Produces<FileResponse>(StatusCodes.Status201Created)
            // The body is the file's raw bytes, not JSON. `*/*` is deliberate: the request's
            // Content-Type is *data* here (it becomes the stored file's mime type), and naming a
            // concrete type instead makes it an endpoint-matching constraint — an upload of a PNG
            // would 404 rather than reach the handler.
            .Accepts<Stream>("*/*");
        group.MapGet("/buckets/{bucketId}/files", ListFiles).Produces<FileListResponse>();
        group.MapGet("/buckets/{bucketId}/files/{fileId}", GetFile).Produces<FileResponse>();
        group.MapGet("/buckets/{bucketId}/files/{fileId}/download", DownloadFile)
            // `Produces<Stream>`, not a bare `Produces(200, contentType: …)`: without a response
            // *type* the generator emits no `content` block at all, and the endpoint reads as
            // undocumented (caught by OpenApiDocumentTests, which exists for exactly this).
            .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream")
            .Produces<Stream>(StatusCodes.Status206PartialContent, "application/octet-stream");
        group.MapPatch("/buckets/{bucketId}/files/{fileId}", UpdateFile).Produces<FileResponse>();
        // Storage Phase 3: replaces the file's bytes in place (same id) rather than creating a new
        // file — the capability re-upload-and-purge-derivatives needs. `*/*` for the same reason
        // CreateFile's Accept is: the body is the new bytes, and the request's Content-Type becomes
        // the stored file's mime type.
        group.MapPut("/buckets/{bucketId}/files/{fileId}", ReplaceFile)
            .Produces<FileResponse>()
            .Accepts<Stream>("*/*");
        group.MapDelete("/buckets/{bucketId}/files/{fileId}", DeleteFile).Produces(StatusCodes.Status204NoContent);

        group.MapGet("/buckets/{bucketId}/files/{fileId}/permissions", GetFilePermissions)
            .Produces<FilePermissionsResponse>();
        group.MapPatch("/buckets/{bucketId}/files/{fileId}/permissions", UpdateFilePermissions)
            .Produces<FilePermissionsResponse>();
    }

    // ---- buckets ---------------------------------------------------------------------------

    private static async Task<IResult> CreateBucket(
        CreateBucketRequest req, HttpContext http, BucketsService buckets, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.StorageWrite);
        var bucket = await buckets.CreateAsync(
            project.Id, req.Key, req.Name, req.MaxFileSizeBytes, req.AllowedMimeTypes,
            req.FileSecurity, req.InlineTypes, ct);
        return Results.Created($"/v1/storage/buckets/{Ids.Wire(bucket.Id)}", BucketResponse.From(bucket));
    }

    private static async Task<IResult> ListBuckets(HttpContext http, BucketsService buckets, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.StorageRead);
        var list = await buckets.ListAsync(project.Id, ct);
        return Results.Ok(new BucketListResponse(list.Count, [.. list.Select(BucketResponse.From)]));
    }

    private static async Task<IResult> GetBucket(
        string bucketId, HttpContext http, BucketsService buckets, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.StorageRead);
        return Results.Ok(BucketResponse.From(await buckets.GetAsync(project.Id, bucketId, ct)));
    }

    private static async Task<IResult> UpdateBucket(
        string bucketId, UpdateBucketRequest req, HttpContext http, BucketsService buckets, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.StorageWrite);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        bucket = await buckets.UpdateAsync(
            bucket, req.Name, req.Enabled, req.MaxFileSizeBytes, req.AllowedMimeTypes,
            clearAllowedMimeTypes: false, req.FileSecurity, req.InlineTypes, ct);
        return Results.Ok(BucketResponse.From(bucket));
    }

    private static async Task<IResult> DeleteBucket(
        string bucketId, HttpContext http, BucketsService buckets, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.StorageWrite);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        await buckets.DeleteAsync(bucket, SchemaLookup.TryParseForce(http), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPermissions(
        string bucketId, HttpContext http, BucketsService buckets, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.StorageRead);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        return Results.Ok(new BucketPermissionsResponse(await buckets.GetPermissionsAsync(bucket.Id, ct)));
    }

    private static async Task<IResult> UpdatePermissions(
        string bucketId, UpdateBucketPermissionsRequest req, HttpContext http,
        BucketsService buckets, CancellationToken ct)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.StorageWrite);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        return Results.Ok(new BucketPermissionsResponse(
            await buckets.ReplacePermissionsAsync(bucket.Id, req.Permissions ?? [], ct)));
    }

    // ---- files -----------------------------------------------------------------------------

    private static async Task<IResult> CreateFile(
        string bucketId, HttpContext http, BucketsService buckets, FilesService files,
        QuotaService quotas, IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.StorageWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var (roles, bypass) = await RowEndpoints.CallerAsync(http, roleResolver);
        var file = await StorageTransfer.UploadAsync(http, quotas, files, bucket, roles, bypass, ct);
        return Results.Created(
            $"/v1/storage/buckets/{bucketId}/files/{Ids.Wire(file.Id)}",
            FileResponse.From(file, await files.GetFilePermissionsAsync(bucket, file.Id, ct)));
    }

    private static async Task<IResult> ListFiles(
        string bucketId, HttpContext http, BucketsService buckets, FilesService files,
        IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.StorageRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var (roles, bypass) = await RowEndpoints.CallerAsync(http, roleResolver);
        var (total, list) = await files.ListAsync(bucket, Limit(http), Offset(http), roles, bypass, ct);
        return Results.Ok(await FileListResponse.FromAsync(files, bucket, total, list, ct));
    }

    private static async Task<IResult> GetFile(
        string bucketId, string fileId, HttpContext http, BucketsService buckets, FilesService files,
        IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.StorageRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var (roles, bypass) = await RowEndpoints.CallerAsync(http, roleResolver);
        var file = await files.GetAsync(bucket, fileId, roles, bypass, ct);
        return Results.Ok(FileResponse.From(file, await files.GetFilePermissionsAsync(bucket, file.Id, ct)));
    }

    private static async Task<IResult> DownloadFile(
        string bucketId, string fileId, HttpContext http, BucketsService buckets, FilesService files,
        IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.StorageRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var (roles, bypass) = await RowEndpoints.CallerAsync(http, roleResolver);
        var download = await files.OpenDownloadAsync(
            bucket, fileId, http.Request.Headers.Range, StorageTransfer.ParseTransform(http), roles, bypass, ct);
        return await StorageTransfer.DownloadAsync(http, bucket, download, ct);
    }

    private static async Task<IResult> UpdateFile(
        string bucketId, string fileId, UpdateFileRequest req, HttpContext http,
        BucketsService buckets, FilesService files, IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.StorageWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var (roles, bypass) = await RowEndpoints.CallerAsync(http, roleResolver);
        var file = await files.RenameAsync(bucket, fileId, req.Name, roles, bypass, ct);
        return Results.Ok(FileResponse.From(file, await files.GetFilePermissionsAsync(bucket, file.Id, ct)));
    }

    private static async Task<IResult> ReplaceFile(
        string bucketId, string fileId, HttpContext http, BucketsService buckets, FilesService files,
        QuotaService quotas, IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.StorageWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var (roles, bypass) = await RowEndpoints.CallerAsync(http, roleResolver);
        var file = await StorageTransfer.ReplaceAsync(http, quotas, files, bucket, fileId, roles, bypass, ct);
        return Results.Ok(FileResponse.From(file, await files.GetFilePermissionsAsync(bucket, file.Id, ct)));
    }

    private static async Task<IResult> DeleteFile(
        string bucketId, string fileId, HttpContext http, BucketsService buckets, FilesService files,
        IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.StorageWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var (roles, bypass) = await RowEndpoints.CallerAsync(http, roleResolver);
        await files.DeleteAsync(bucket, fileId, roles, bypass, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetFilePermissions(
        string bucketId, string fileId, HttpContext http, BucketsService buckets, FilesService files,
        IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.StorageRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var (roles, bypass) = await RowEndpoints.CallerAsync(http, roleResolver);
        var file = await files.GetAsync(bucket, fileId, roles, bypass, ct);
        return Results.Ok(new FilePermissionsResponse(await files.GetFilePermissionsAsync(bucket, file.Id, ct)));
    }

    /// <summary>
    /// Gated on <c>update</c> for the file itself, not on a bucket-management scope — which is the
    /// difference between this and the bucket permission matrix above. Re-sharing a file you own is
    /// an end-user action; reconfiguring the bucket is not.
    /// </summary>
    private static async Task<IResult> UpdateFilePermissions(
        string bucketId, string fileId, UpdateFilePermissionsRequest req, HttpContext http,
        BucketsService buckets, FilesService files, IRoleResolver roleResolver, CancellationToken ct)
    {
        RequireScopeIfKey(http, ApiKeyScopes.StorageWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var (roles, bypass) = await RowEndpoints.CallerAsync(http, roleResolver);
        return Results.Ok(new FilePermissionsResponse(await files.ReplaceFilePermissionsAsync(
            bucket, fileId, req.Permissions ?? [], roles, bypass, ct)));
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>Sessions and guests reach file endpoints on bucket permissions alone; a key additionally needs the scope.</summary>
    private static void RequireScopeIfKey(HttpContext http, string scope)
    {
        if (AppPrincipalFilter.Current(http) is RequestPrincipal.Key)
            AppPrincipalFilter.RequireScope(http, scope);
    }

    private static int Limit(HttpContext http) =>
        int.TryParse(http.Request.Query["limit"], out var limit) ? limit : 25;

    private static int Offset(HttpContext http) =>
        int.TryParse(http.Request.Query["offset"], out var offset) && offset > 0 ? offset : 0;
}
