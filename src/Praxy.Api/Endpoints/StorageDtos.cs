using Praxy.Core;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record CreateBucketRequest(
    string Key, string Name, long? MaxFileSizeBytes, string[]? AllowedMimeTypes);

public sealed record UpdateBucketRequest(
    string? Name, bool? Enabled, long? MaxFileSizeBytes, string[]? AllowedMimeTypes);

public sealed record UpdateBucketPermissionsRequest(string[] Permissions);

public sealed record UpdateFileRequest(string? Name);

public sealed record BucketResponse(
    string Id, string Key, string Name, bool Enabled, long MaxFileSizeBytes,
    IReadOnlyList<string>? AllowedMimeTypes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static BucketResponse From(Bucket b) => new(
        Ids.Wire(b.Id), b.Key, b.Name, b.Enabled, b.MaxFileSizeBytes, b.AllowedMimeTypes,
        b.CreatedAt, b.UpdatedAt);
}

/// <summary>
/// File metadata. <c>chunkSizeBytes</c>/<c>chunkCount</c> are reported because they are what the
/// file was actually written with, not what config currently says — the distinction matters the
/// first time an operator changes <c>Praxy:Storage:ChunkSizeBytes</c>.
/// </summary>
public sealed record FileResponse(
    string Id, string BucketId, string Name, string MimeType, long SizeBytes,
    int ChunkSizeBytes, int ChunkCount, string Checksum, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static FileResponse From(StoredFile f) => new(
        Ids.Wire(f.Id), Ids.Wire(f.BucketId), f.Name, f.MimeType, f.SizeBytes,
        f.ChunkSizeBytes, f.ChunkCount, f.Checksum, f.CreatedAt, f.UpdatedAt);
}

public sealed record BucketListResponse(int Total, IReadOnlyList<BucketResponse> Buckets);

public sealed record BucketPermissionsResponse(IReadOnlyList<string> Permissions);

public sealed record FileListResponse(int Total, IReadOnlyList<FileResponse> Files);

/// <summary>What a project has stored versus its <c>MaxStorageBytesPerProject</c> quota — the console's usage bar.</summary>
public sealed record StorageUsageResponse(long UsedBytes, long MaxBytes, long MaxFileSizeBytes);
