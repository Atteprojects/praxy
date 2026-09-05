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
    /// The exact analogue of <see cref="TableDef.RowSecurity"/>: opts this bucket into per-file
    /// grants *on top of* the bucket-level matrix. Additive, never restrictive — a bucket-level
    /// grant still reaches every file, and no <see cref="FilePermission"/> row can claw it back
    /// (docs/research/storage.md). "Users see only their own uploads" is therefore configured by
    /// granting no bucket-level read at all and attaching a per-file one.
    /// </summary>
    public bool FileSecurity { get; set; }

    /// <summary>
    /// Types this bucket may serve <c>inline</c> instead of as an attachment. Empty/null (the
    /// default) means every download is an attachment. Never trusted on its own: a response is
    /// inline only when the type is in here *and* in <c>InlineTypes.Safe</c>, the hard-coded set
    /// that can never contain <c>text/html</c> or <c>image/svg+xml</c> — a file's stored MIME type
    /// is whatever the uploader sent, so the allowlist is what makes rendering it safe.
    /// </summary>
    public string[]? InlineTypes { get; set; }

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
/// Per-file permission grant — <see cref="TablePermission"/>'s field shape verbatim again, one
/// level down, and the storage analogue of a row's <c>__perms</c> side table. Only consulted when
/// the owning bucket has <see cref="Bucket.FileSecurity"/> on, and only *after* the bucket-level
/// matrix has already failed to grant the action: these rows widen access, they never narrow it.
/// </summary>
public class FilePermission
{
    public required Guid FileId { get; set; }

    /// <summary>read | update | delete — a file cannot grant its own creation, exactly like a row.</summary>
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
    /// Storage Phase 3: the source's decoded pixel dimensions, probed and cached here the first time
    /// any transform request needs them (never at upload time — plenty of files are never
    /// transformed) rather than on every request that omits one axis, which would otherwise mean
    /// re-reading and re-parsing the whole source on every such request forever. Null for every file
    /// until then, and for every file that is never transformed at all. Cleared back to null by
    /// <c>FilesService.ReplaceBytesAsync</c> — the probe is only valid for the bytes it measured.
    /// </summary>
    public int? Width { get; set; }
    public int? Height { get; set; }

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

/// <summary>
/// Storage Phase 3: one cached image transform of a <see cref="StoredFile"/> — a representation of
/// that file, not a resource of its own (docs/research/storage.md). It carries no permissions of its
/// own for exactly that reason: every read resolves through the source file's own
/// <c>FileAccessRules</c> decision, never a second check.
///
/// <c>(FileId, Width, Height, Format, Quality, Gravity)</c> is unique — the cache key
/// <see cref="Praxy.Storage.ImageTransforms.Resolve"/> computes — so a second request for the same
/// transform finds this row instead of generating again. <c>Quality</c> is a real column value
/// (<c>0</c> for lossless <c>png</c>, never <c>null</c>) rather than nullable, because Postgres
/// treats every <c>NULL</c> in a unique index as distinct and would otherwise let duplicate <c>png</c>
/// derivatives through.
/// </summary>
public class FileDerivative
{
    public required Guid Id { get; set; }
    public required Guid FileId { get; set; }
    public required int Width { get; set; }
    public required int Height { get; set; }

    /// <summary>png | jpeg | webp — the encoder's choice, never the uploader's, which is what makes a derivative's type safer than its source's (docs/research/storage.md).</summary>
    public required string Format { get; set; }

    /// <summary>0 (never meaningful) for png; 1-100 for jpeg/webp. See the type-level remarks for why this isn't nullable.</summary>
    public required int Quality { get; set; }

    /// <summary>
    /// The crop anchor — always a real value from <c>ImageTransforms.Gravities</c>, normalized to
    /// <c>"center"</c> (never null) whenever this derivative isn't cropped, since gravity has no
    /// visual effect there and letting it vary anyway would fragment the cache.
    /// </summary>
    public required string Gravity { get; set; }

    public required string MimeType { get; set; }
    public long SizeBytes { get; set; }
    public required int ChunkSizeBytes { get; set; }
    public int ChunkCount { get; set; }
    public required string Checksum { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A derivative's own chunk rows — the exact analogue of <see cref="FileChunk"/> one level down, in a
/// separate table rather than <c>file_chunks</c> itself: that table's FK targets <c>files.id</c>, and
/// a derivative is deliberately not a row in <c>files</c> (it would then be listable, permissionable,
/// and quota-countable as an independent resource, which is precisely what "a representation, not a
/// resource of its own" rules out). Same chunk-and-stream shape behind the same <c>IFileStore</c>
/// seam, addressed by the derivative's own id instead of a file's.
/// </summary>
public class FileDerivativeChunk
{
    public required Guid DerivativeId { get; set; }
    public required int Index { get; set; }
    public required byte[] Data { get; set; }
}
