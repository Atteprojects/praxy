namespace Praxy.Persistence.Entities;

/// <summary>
/// A file container: the configuration *and* permission boundary, deliberately the same shape a
/// <see cref="TableDef"/> has (docs/research/storage.md's resource model — bucket ≈ table,
/// file ≈ row). Deny-by-default like every other resource: a new bucket is unreachable until
/// <see cref="BucketPermission"/> rows grant a role.
/// </summary>
public class Bucket
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Reserved for Storage Phase 2's per-file permissions — the exact analogue of
    /// <see cref="TableDef.RowSecurity"/>, opting a bucket into per-file grants on top of the
    /// bucket-level matrix. Persisted from Phase 1 (it is part of the design doc's field list) but
    /// never read or exposed on the wire yet: Phase 1 is bucket-level only.
    /// </summary>
    public bool FileSecurity { get; set; }

    /// <summary>Per-file ceiling for this bucket. Never above the resolved <c>MaxFileSizeBytes</c> quota, which is the instance/org-level cap.</summary>
    public required long MaxFileSizeBytes { get; set; }

    /// <summary>Null means any type is accepted; an empty array is normalized to null on write.</summary>
    public string[]? AllowedMimeTypes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Bucket-level permission grant — <see cref="TablePermission"/>'s field shape verbatim, over the
/// same four storable actions. <c>write</c> is never stored; it is expanded to create+update+delete
/// at write time by the same <c>PermissionStrings</c> parser tables use.
/// </summary>
public class BucketPermission
{
    public required Guid BucketId { get; set; }

    /// <summary>read | create | update | delete</summary>
    public required string Action { get; set; }

    public required string Role { get; set; }
}

/// <summary>
/// One stored file's metadata. The bytes live in <see cref="FileChunk"/> rows behind
/// <c>IFileStore</c>; nothing above that seam knows a chunk exists.
/// </summary>
public class StoredFile
{
    public required Guid Id { get; set; }
    public required Guid BucketId { get; set; }
    public required string Name { get; set; }
    public required string MimeType { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>
    /// The chunk size this file was actually written with, recorded per file rather than read from
    /// config at read time. Changing <c>Praxy:Storage:ChunkSizeBytes</c> is therefore a tuning
    /// change for *new* uploads only and can never invalidate a byte already stored.
    /// </summary>
    public required int ChunkSizeBytes { get; set; }

    public int ChunkCount { get; set; }

    /// <summary>Lowercase hex SHA-256, computed while streaming the upload — never by re-reading the stored bytes.</summary>
    public required string Checksum { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One fixed-size slice of a file's bytes. PK is <c>(file_id, index)</c> so a read is an ordered
/// index scan and a Phase 2 <c>Range</c> request can seek straight to <c>offset / chunk_size</c>;
/// the FK cascades so deleting a file removes its bytes in the same statement.
/// </summary>
public class FileChunk
{
    public required Guid FileId { get; set; }

    /// <summary>Zero-based position of this chunk within the file.</summary>
    public required int Index { get; set; }

    /// <summary>
    /// <c>bytea</c>, forced to <c>STORAGE EXTERNAL</c> by the migration: the default
    /// (<c>EXTENDED</c>) tries to LZ-compress every value before storing it out of line, which is
    /// pure CPU burn for the already-compressed media (JPEG/PNG/MP4/ZIP) a real file store is made
    /// of. See <c>StorageEngineTests.File_chunk_data_column_uses_external_storage</c> — the setting
    /// is invisible from behavior alone, so it is asserted rather than assumed.
    /// </summary>
    public required byte[] Data { get; set; }
}
