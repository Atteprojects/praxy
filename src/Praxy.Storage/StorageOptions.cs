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
    long DefaultBucketMaxFileSizeBytes = 52_428_800,
    /// <summary>
    /// Storage Phase 3: the decoded pixel-count ceiling a source image must pass *before*
    /// <see cref="ImageTransformer"/> allocates the full decode. Checked against the header
    /// (<c>SKCodec.Info</c>), never the encoded byte size — a small file can still claim an enormous
    /// decoded size, which is exactly what a decompression bomb is. 40 megapixels comfortably covers
    /// real camera/scan output (a 45 MP full-frame sensor is the practical ceiling most photographers
    /// hit) while still rejecting the pathological case.
    /// </summary>
    long MaxSourceImagePixels = 40_000_000);
