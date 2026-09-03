namespace Praxy.Storage;

/// <summary>
/// Every knob configurable, per CLAUDE.md's cross-phase rule — bound from <c>Praxy:Storage:*</c>
/// config in Program.cs, same plain-record-of-defaults shape as <c>SitesOptions</c>/<c>FunctionsOptions</c>.
/// </summary>
public sealed record StorageOptions(
    /// <summary>
    /// The chunk size *new* uploads are written with. 512 KiB: large enough that a 100 MB file is
    /// ~200 rows rather than thousands, small enough that a single chunk is a comfortable buffer
    /// (docs/research/storage.md calls this a tuning constant, not an invariant). Recorded on every
    /// file as <c>chunk_size_bytes</c>, so changing this never invalidates a byte already stored.
    /// </summary>
    int ChunkSizeBytes = 524_288,
    /// <summary>
    /// Default per-file ceiling applied to a bucket that doesn't set its own. Always clamped to the
    /// resolved <c>MaxFileSizeBytes</c> quota, which is the real cap.
    /// </summary>
    long DefaultBucketMaxFileSizeBytes = 52_428_800);
