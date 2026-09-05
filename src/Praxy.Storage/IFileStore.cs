namespace Praxy.Storage;

/// <summary>
/// Where a file's bytes actually live. Deliberately narrow — open a write stream, open a read
/// stream, delete — so the one implementation that exists (Postgres chunk rows) can be joined or
/// replaced later by a disk or S3-compatible backend *without touching the API surface, the
/// metadata model, or the permission path* (docs/research/storage.md). Nothing above this seam
/// knows a chunk exists.
/// </summary>
public interface IFileStore
{
    /// <summary>
    /// A write stream for a file whose metadata row already exists. The caller copies the request
    /// body into it and then calls <see cref="FileWriteStream.CompleteAsync"/>; nothing is durable
    /// until the caller's own transaction commits.
    /// </summary>
    FileWriteStream OpenWrite(Guid fileId, int chunkSizeBytes);

    /// <summary>
    /// A forward-only read stream over <paramref name="length"/> bytes of the file starting at
    /// <paramref name="offset"/> (the whole file, in order, with the defaults). Never materializes
    /// it — the caller copies it straight to a response body.
    ///
    /// <para>
    /// The range is part of the seam rather than something the endpoint does above it, and that is
    /// deliberate: skipping bytes off the front of a full stream works for the Postgres backend and
    /// would quietly ruin the next one, since an S3-compatible store serves a range with a native
    /// ranged GET and would otherwise have to fetch a whole object to answer 1 KB
    /// (docs/research/storage.md).
    /// </para>
    ///
    /// <para>
    /// <paramref name="chunkSizeBytes"/> is the file's own recorded chunk size — passed in, exactly
    /// as <see cref="OpenWrite"/> takes it, because the caller already holds the metadata row and a
    /// store re-reading it would be both a second round trip and a second source of truth. A
    /// backend that doesn't chunk ignores it.
    /// </para>
    /// </summary>
    Stream OpenRead(Guid fileId, int chunkSizeBytes, long offset = 0, long? length = null);

    /// <summary>
    /// Drops a file's bytes. Deleting the metadata row cascades to the same rows, so this exists
    /// for the case where bytes must go without the metadata (a rolled-back re-upload) rather than
    /// as the normal delete path.
    /// </summary>
    Task DeleteAsync(Guid fileId, CancellationToken ct);
}

/// <summary>
/// A write-only <see cref="Stream"/> that turns a byte stream into whatever the backing store
/// stores, tracking the numbers the metadata row needs as the bytes go past — so the checksum is
/// computed *while* streaming rather than by re-reading what was just written.
/// </summary>
public abstract class FileWriteStream : Stream
{
    /// <summary>Total bytes accepted so far.</summary>
    public abstract long BytesWritten { get; }

    /// <summary>Chunks flushed so far; final only after <see cref="CompleteAsync"/>.</summary>
    public abstract int ChunkCount { get; }

    /// <summary>Lowercase hex SHA-256 of everything written. Valid only after <see cref="CompleteAsync"/>.</summary>
    public abstract string Checksum { get; }

    /// <summary>Flushes the trailing partial chunk (if any) and finalizes <see cref="Checksum"/>.</summary>
    public abstract Task CompleteAsync(CancellationToken ct);

    public sealed override bool CanRead => false;
    public sealed override bool CanSeek => false;
    public sealed override bool CanWrite => true;
    public override long Length => BytesWritten;

    public sealed override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public sealed override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public sealed override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public sealed override void SetLength(long value) => throw new NotSupportedException();
}
