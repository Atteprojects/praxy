using System.Text.Json.Serialization;
using Praxy.Core;
using Praxy.Persistence.Entities;
using Praxy.Storage;

namespace Praxy.Api.Endpoints;

public sealed record CreateBucketRequest(
    string Key, string Name, long? MaxFileSizeBytes, string[]? AllowedMimeTypes,
    bool? FileSecurity, string[]? InlineTypes);

public sealed record UpdateBucketRequest(
    string? Name, bool? Enabled, long? MaxFileSizeBytes, string[]? AllowedMimeTypes,
    bool? FileSecurity, string[]? InlineTypes);

public sealed record UpdateBucketPermissionsRequest(string[] Permissions);

public sealed record UpdateFilePermissionsRequest(string[] Permissions);

public sealed record UpdateFileRequest(string? Name);

public sealed record BucketResponse(
    string Id, string Key, string Name, bool Enabled, bool FileSecurity, long MaxFileSizeBytes,
    IReadOnlyList<string>? AllowedMimeTypes, IReadOnlyList<string> InlineTypes,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static BucketResponse From(Bucket b) => new(
        Ids.Wire(b.Id), b.Key, b.Name, b.Enabled, b.FileSecurity, b.MaxFileSizeBytes, b.AllowedMimeTypes,
        // Always an array, never absent: "nothing is served inline" is the default state of a real
        // setting, not a missing one — the console renders it as an empty allow-list either way.
        b.InlineTypes ?? [], b.CreatedAt, b.UpdatedAt);
}

/// <summary>
/// File metadata. <c>chunkSizeBytes</c>/<c>chunkCount</c> are reported because they are what the
/// file was actually written with, not what config currently says — the distinction matters the
/// first time an operator changes <c>Praxy:Storage:ChunkSizeBytes</c>.
/// </summary>
public sealed record FileResponse(
    string Id, string BucketId, string Name, string MimeType, long SizeBytes,
    int ChunkSizeBytes, int ChunkCount, string Checksum, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    /// <summary>
    /// The file's own grants, named <c>$permissions</c> to match a row's — same grammar, same
    /// meaning, and empty whenever the bucket has <c>file_security</c> off, because nothing
    /// consults them then.
    /// </summary>
    [property: JsonPropertyName("$permissions")] IReadOnlyList<string> Permissions)
{
    public static FileResponse From(StoredFile f, IReadOnlyList<string>? permissions = null) => new(
        Ids.Wire(f.Id), Ids.Wire(f.BucketId), f.Name, f.MimeType, f.SizeBytes,
        f.ChunkSizeBytes, f.ChunkCount, f.Checksum, f.CreatedAt, f.UpdatedAt, permissions ?? []);
}

public sealed record BucketListResponse(int Total, IReadOnlyList<BucketResponse> Buckets);

public sealed record BucketPermissionsResponse(IReadOnlyList<string> Permissions);

public sealed record FilePermissionsResponse(IReadOnlyList<string> Permissions);

/// <summary>
/// The types this build will serve <c>inline</c> — the console's picker for a bucket's
/// <c>inlineTypes</c>, fetched rather than hard-coded a second time in TypeScript so the two can't
/// drift. Same shape as the functions surface's <c>/runtimes</c>.
/// </summary>
public sealed record InlineTypeListResponse(IReadOnlyList<string> Types);

public sealed record FileListResponse(int Total, IReadOnlyList<FileResponse> Files)
{
    /// <summary>
    /// One page, with every file's <c>$permissions</c> attached in a single extra query rather than
    /// one per row — the same batching <c>RowsService.AttachPermissionsAsync</c> does, and it costs
    /// nothing at all on a bucket with <c>file_security</c> off.
    /// </summary>
    public static async Task<FileListResponse> FromAsync(
        FilesService files, Bucket bucket, int total, IReadOnlyList<StoredFile> page, CancellationToken ct)
    {
        var permissions = await files.GetFilePermissionsAsync(bucket, [.. page.Select(f => f.Id)], ct);
        return new FileListResponse(total,
            [.. page.Select(f => FileResponse.From(f, permissions.GetValueOrDefault(f.Id, [])))]);
    }
}

/// <summary>What a project has stored versus its <c>MaxStorageBytesPerProject</c> quota — the console's usage bar.</summary>
public sealed record StorageUsageResponse(long UsedBytes, long MaxBytes, long MaxFileSizeBytes);
