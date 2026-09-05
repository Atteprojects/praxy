using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Storage;
using Praxy.Tables.Quotas;

namespace Praxy.Api.Endpoints;

/// <summary>
/// The console's storage surface: operator session + project ownership, exactly like
/// <see cref="ConsoleRowEndpoints"/>. Operators manage the whole project, so file reads/writes here
/// bypass bucket permission filtering entirely — the same posture as an API key with
/// <c>bypassRowPermissions</c>, implicit for the console's own operator auth. Every mutation is
/// audited.
/// </summary>
public static class ConsoleStorageEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/v1/console/projects/{projectId}/storage")
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ConsoleProjectFilter>();

        admin.MapGet("/usage", GetUsage).Produces<StorageUsageResponse>();
        // Server-owned vocabulary, fetched like the functions surface's /runtimes rather than
        // hard-coded again in the console — the two copies would drift, and this one is a security
        // boundary.
        admin.MapGet("/inline-types", ListInlineTypes).Produces<InlineTypeListResponse>();

        admin.MapGet("/buckets", ListBuckets).Produces<BucketListResponse>();
        admin.MapPost("/buckets", CreateBucket).Produces<BucketResponse>(StatusCodes.Status201Created);
        admin.MapGet("/buckets/{bucketId}", GetBucket).Produces<BucketResponse>();
        admin.MapPatch("/buckets/{bucketId}", UpdateBucket).Produces<BucketResponse>();
        admin.MapDelete("/buckets/{bucketId}", DeleteBucket).Produces(StatusCodes.Status204NoContent);

        admin.MapGet("/buckets/{bucketId}/permissions", GetPermissions).Produces<BucketPermissionsResponse>();
        admin.MapPatch("/buckets/{bucketId}/permissions", UpdatePermissions).Produces<BucketPermissionsResponse>();

        admin.MapGet("/buckets/{bucketId}/files", ListFiles).Produces<FileListResponse>();
        admin.MapPost("/buckets/{bucketId}/files", CreateFile)
            .Produces<FileResponse>(StatusCodes.Status201Created)
            // See StorageEndpoints' remarks: `*/*`, because the Content-Type is the file's own.
            .Accepts<Stream>("*/*");
        admin.MapGet("/buckets/{bucketId}/files/{fileId}", GetFile).Produces<FileResponse>();
        admin.MapGet("/buckets/{bucketId}/files/{fileId}/download", DownloadFile)
            // `Produces<Stream>`, not a bare `Produces(200, contentType: …)`: without a response
            // *type* the generator emits no `content` block at all, and the endpoint reads as
            // undocumented (caught by OpenApiDocumentTests, which exists for exactly this).
            .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream")
            .Produces<Stream>(StatusCodes.Status206PartialContent, "application/octet-stream");
        admin.MapPatch("/buckets/{bucketId}/files/{fileId}", UpdateFile).Produces<FileResponse>();
        admin.MapDelete("/buckets/{bucketId}/files/{fileId}", DeleteFile).Produces(StatusCodes.Status204NoContent);

        admin.MapGet("/buckets/{bucketId}/files/{fileId}/permissions", GetFilePermissions)
            .Produces<FilePermissionsResponse>();
        admin.MapPatch("/buckets/{bucketId}/files/{fileId}/permissions", UpdateFilePermissions)
            .Produces<FilePermissionsResponse>();
    }

    // ---- usage -----------------------------------------------------------------------------

    private static async Task<IResult> GetUsage(HttpContext http, QuotaService quotas, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var budget = await quotas.GetStorageBudgetAsync(project.Id, ct);
        return Results.Ok(new StorageUsageResponse(
            budget.UsedBytes, budget.MaxTotalBytes, budget.MaxFileSizeBytes));
    }

    private static IResult ListInlineTypes() => Results.Ok(new InlineTypeListResponse(InlineTypes.Safe));

    // ---- buckets ---------------------------------------------------------------------------

    private static async Task<IResult> ListBuckets(HttpContext http, BucketsService buckets, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var list = await buckets.ListAsync(project.Id, ct);
        return Results.Ok(new BucketListResponse(list.Count, [.. list.Select(BucketResponse.From)]));
    }

    private static async Task<IResult> CreateBucket(
        CreateBucketRequest req, HttpContext http, PraxyDb db, BucketsService buckets, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.CreateAsync(
            project.Id, req.Key, req.Name, req.MaxFileSizeBytes, req.AllowedMimeTypes,
            req.FileSecurity, req.InlineTypes, ct);
        await AuditAsync(db, http, project.Id, "storage.buckets.create", $"bucket/{Ids.Wire(bucket.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/storage/buckets/{Ids.Wire(bucket.Id)}", BucketResponse.From(bucket));
    }

    private static async Task<IResult> GetBucket(
        string bucketId, HttpContext http, BucketsService buckets, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        return Results.Ok(BucketResponse.From(await buckets.GetAsync(project.Id, bucketId, ct)));
    }

    private static async Task<IResult> UpdateBucket(
        string bucketId, UpdateBucketRequest req, HttpContext http, PraxyDb db,
        BucketsService buckets, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        // The console's mime-type editor sends [] to mean "accept anything again"; the data-plane
        // API keeps null-means-unchanged, so only this surface opts into the clearing behavior.
        bucket = await buckets.UpdateAsync(
            bucket, req.Name, req.Enabled, req.MaxFileSizeBytes,
            req.AllowedMimeTypes is { Length: > 0 } ? req.AllowedMimeTypes : null,
            clearAllowedMimeTypes: req.AllowedMimeTypes is { Length: 0 },
            req.FileSecurity, req.InlineTypes, ct);
        await AuditAsync(db, http, project.Id, "storage.buckets.update", $"bucket/{bucketId}", ct);
        return Results.Ok(BucketResponse.From(bucket));
    }

    private static async Task<IResult> DeleteBucket(
        string bucketId, HttpContext http, PraxyDb db, BucketsService buckets, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        await buckets.DeleteAsync(bucket, SchemaLookup.TryParseForce(http), ct);
        await AuditAsync(db, http, project.Id, "storage.buckets.delete", $"bucket/{bucketId}", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPermissions(
        string bucketId, HttpContext http, BucketsService buckets, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        return Results.Ok(new BucketPermissionsResponse(await buckets.GetPermissionsAsync(bucket.Id, ct)));
    }

    private static async Task<IResult> UpdatePermissions(
        string bucketId, UpdateBucketPermissionsRequest req, HttpContext http, PraxyDb db,
        BucketsService buckets, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var permissions = await buckets.ReplacePermissionsAsync(bucket.Id, req.Permissions ?? [], ct);
        await AuditAsync(db, http, project.Id, "storage.buckets.permissions", $"bucket/{bucketId}", ct);
        return Results.Ok(new BucketPermissionsResponse(permissions));
    }

    // ---- files -----------------------------------------------------------------------------

    private static async Task<IResult> ListFiles(
        string bucketId, HttpContext http, BucketsService buckets, FilesService files, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var limit = int.TryParse(http.Request.Query["limit"], out var l) ? l : 50;
        var offset = int.TryParse(http.Request.Query["offset"], out var o) && o > 0 ? o : 0;
        var (total, list) = await files.ListAsync(bucket, limit, offset, [], bypassPermissions: true, ct);
        return Results.Ok(await FileListResponse.FromAsync(files, bucket, total, list, ct));
    }

    private static async Task<IResult> CreateFile(
        string bucketId, HttpContext http, PraxyDb db, BucketsService buckets, FilesService files,
        QuotaService quotas, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var file = await StorageTransfer.UploadAsync(
            http, quotas, files, bucket, [], bypassPermissions: true, ct);
        await AuditAsync(db, http, project.Id, "storage.files.create",
            $"bucket/{bucketId}/file/{Ids.Wire(file.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/storage/buckets/{bucketId}/files/{Ids.Wire(file.Id)}",
            FileResponse.From(file, await files.GetFilePermissionsAsync(bucket, file.Id, ct)));
    }

    private static async Task<IResult> GetFile(
        string bucketId, string fileId, HttpContext http, BucketsService buckets, FilesService files, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var file = await files.GetAsync(bucket, fileId, [], bypassPermissions: true, ct);
        return Results.Ok(FileResponse.From(file, await files.GetFilePermissionsAsync(bucket, file.Id, ct)));
    }

    private static async Task<IResult> DownloadFile(
        string bucketId, string fileId, HttpContext http, BucketsService buckets, FilesService files, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var (file, range, content) = await files.OpenDownloadAsync(
            bucket, fileId, http.Request.Headers.Range, [], bypassPermissions: true, ct);
        return await StorageTransfer.DownloadAsync(http, bucket, file, range, content, ct);
    }

    private static async Task<IResult> UpdateFile(
        string bucketId, string fileId, UpdateFileRequest req, HttpContext http, PraxyDb db,
        BucketsService buckets, FilesService files, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var file = await files.RenameAsync(bucket, fileId, req.Name, [], bypassPermissions: true, ct);
        await AuditAsync(db, http, project.Id, "storage.files.update", $"bucket/{bucketId}/file/{fileId}", ct);
        return Results.Ok(FileResponse.From(file, await files.GetFilePermissionsAsync(bucket, file.Id, ct)));
    }

    private static async Task<IResult> DeleteFile(
        string bucketId, string fileId, HttpContext http, PraxyDb db,
        BucketsService buckets, FilesService files, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        await files.DeleteAsync(bucket, fileId, [], bypassPermissions: true, ct);
        await AuditAsync(db, http, project.Id, "storage.files.delete", $"bucket/{bucketId}/file/{fileId}", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetFilePermissions(
        string bucketId, string fileId, HttpContext http, BucketsService buckets, FilesService files,
        CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var file = await files.GetAsync(bucket, fileId, [], bypassPermissions: true, ct);
        return Results.Ok(new FilePermissionsResponse(await files.GetFilePermissionsAsync(bucket, file.Id, ct)));
    }

    private static async Task<IResult> UpdateFilePermissions(
        string bucketId, string fileId, UpdateFilePermissionsRequest req, HttpContext http, PraxyDb db,
        BucketsService buckets, FilesService files, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var bucket = await buckets.GetAsync(project.Id, bucketId, ct);
        var permissions = await files.ReplaceFilePermissionsAsync(
            bucket, fileId, req.Permissions ?? [], [], bypassPermissions: true, ct);
        await AuditAsync(db, http, project.Id, "storage.files.permissions",
            $"bucket/{bucketId}/file/{fileId}", ct);
        return Results.Ok(new FilePermissionsResponse(permissions));
    }

    private static async Task AuditAsync(
        PraxyDb db, HttpContext http, string projectId, string action, string resource, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Actor = $"admin:{op.Account.Id}",
            Action = action,
            Resource = resource,
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }
}
