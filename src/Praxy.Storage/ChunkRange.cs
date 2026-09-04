namespace Praxy.Storage;

/// <summary>
/// Which chunk rows cover a byte range, and how much of the first one to drop. The formulae are
/// docs/research/storage.md's, and they use the file's <b>own</b> recorded
/// <c>chunk_size_bytes</c> — never the configured default — so the arithmetic stays exact for
/// files written before the tuning constant was last changed.
///
/// <code>
/// firstChunk  = offset / chunkSize
/// skipInFirst = offset % chunkSize
/// lastChunk   = (offset + length - 1) / chunkSize    // absent for an open-ended read
/// </code>
///
/// Pure, and separate from the store, because this is the part of a Range request that is easy to
/// get subtly wrong (off by a chunk at either end) and impossible to notice without tests: a
/// slightly wrong answer still streams plausible-looking bytes.
/// </summary>
public readonly record struct ChunkRange(int FirstChunk, int SkipInFirstChunk, int? LastChunk)
{
    public static ChunkRange For(long offset, long? length, int chunkSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSizeBytes, 1);

        var first = offset / chunkSizeBytes;
        var skip = (int)(offset % chunkSizeBytes);
        // A zero-length read has no last chunk to name; the caller stops before reading anything.
        var last = length is { } take && take > 0
            ? Clamp((offset + take - 1) / chunkSizeBytes)
            : (int?)null;
        return new ChunkRange(Clamp(first), skip, last);
    }

    /// <summary>A chunk index is an <c>int</c> column while the byte arithmetic above is 64-bit.</summary>
    private static int Clamp(long chunkIndex) => (int)Math.Min(chunkIndex, int.MaxValue);
}
