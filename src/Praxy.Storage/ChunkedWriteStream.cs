using System.Security.Cryptography;

namespace Praxy.Storage;

/// <summary>
/// The chunking itself, with no idea where a chunk goes: buffers exactly one chunk and hands it to
/// <see cref="WriteChunkAsync"/> whenever it fills. Peak memory is one chunk regardless of file
/// size, which is the whole point of chunking — a single <c>bytea</c> value cannot avoid
/// materializing the file.
///
/// Split out from the Postgres implementation so the boundary arithmetic — where the classic
/// off-by-one lives, in a file that is an exact multiple of the chunk size or one byte over — is
/// testable without a database, and so a future non-Postgres <see cref="IFileStore"/> inherits it
/// rather than reimplementing it.
/// </summary>
public abstract class ChunkedWriteStream : FileWriteStream
{
    private readonly byte[] _buffer;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _pending;
    private long _written;
    private int _chunks;
    private string? _checksum;
    private bool _completed;

    protected ChunkedWriteStream(int chunkSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSizeBytes, 1);
        _buffer = new byte[chunkSizeBytes];
    }

    public override long BytesWritten => _written;
    public override int ChunkCount => _chunks;

    public override string Checksum => _checksum
        ?? throw new InvalidOperationException("Checksum is only final after CompleteAsync.");

    /// <summary>Persists one chunk. <paramref name="data"/> is only valid for the duration of the call.</summary>
    protected abstract Task WriteChunkAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        var source = buffer;
        while (!source.IsEmpty)
        {
            var take = Math.Min(_buffer.Length - _pending, source.Length);
            source[..take].CopyTo(_buffer.AsMemory(_pending));
            // Hashed as the bytes go past, never by re-reading what was written.
            _hash.AppendData(_buffer.AsSpan(_pending, take));
            _pending += take;
            _written += take;
            source = source[take..];

            if (_pending == _buffer.Length)
                await FlushChunkAsync(ct);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async Task CompleteAsync(CancellationToken ct)
    {
        if (_completed) return;
        // Only when something is left over. A file that is an exact multiple of the chunk size has
        // already flushed its last full chunk, and a zero-byte file has nothing at all — writing an
        // empty trailing row in either case is the classic off-by-one here.
        if (_pending > 0)
            await FlushChunkAsync(ct);
        _checksum = Convert.ToHexStringLower(_hash.GetHashAndReset());
        _completed = true;
    }

    private async Task FlushChunkAsync(CancellationToken ct)
    {
        await WriteChunkAsync(_chunks, _buffer.AsMemory(0, _pending), ct);
        _chunks++;
        _pending = 0;
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}
