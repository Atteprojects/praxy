using System.Data;
using Npgsql;
using Praxy.Persistence;

namespace Praxy.Storage;

/// <summary>
/// A derivative's bytes, in <c>praxy.file_derivative_chunks</c> — the same chunk-and-stream shape
/// <see cref="PostgresChunkFileStore"/> gives a real file, addressed by the derivative's own id
/// instead of a file's, and kept in a separate table because a derivative is deliberately not a row
/// in <c>files</c> (docs/research/storage.md — "a representation, not a resource of its own").
///
/// <para>
/// Reuses <see cref="PostgresChunkFileStore.CommandAsync"/> for the one thing worth sharing — every
/// statement enlisting in the caller's ambient EF transaction, so a derivative that gets rolled back
/// (an over-quota generation, a failed encode) never leaves orphaned chunk rows either.
/// </para>
/// </summary>
public sealed class PostgresDerivativeChunkFileStore(PraxyDb db) : IFileStore
{
    public FileWriteStream OpenWrite(Guid fileId, int chunkSizeBytes) =>
        new DerivativeChunkWriteStream(db, fileId, chunkSizeBytes);

    /// <summary><paramref name="chunkSizeBytes"/>/<paramref name="offset"/>/<paramref name="length"/> are unused: a derivative is always read whole (Range is a full-file concern that never reaches one, per docs/research/storage.md), so the read stream below has no chunk arithmetic to do.</summary>
    public Stream OpenRead(Guid fileId, int chunkSizeBytes, long offset = 0, long? length = null) =>
        new DerivativeChunkReadStream(db, fileId);

    public async Task DeleteAsync(Guid fileId, CancellationToken ct)
    {
        await using var cmd = await PostgresChunkFileStore.CommandAsync(
            db, "DELETE FROM praxy.file_derivative_chunks WHERE derivative_id = @derivative_id", ct);
        cmd.Parameters.AddWithValue("derivative_id", fileId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

internal sealed class DerivativeChunkWriteStream(PraxyDb db, Guid derivativeId, int chunkSizeBytes)
    : ChunkedWriteStream(chunkSizeBytes)
{
    protected override async Task WriteChunkAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await using var cmd = await PostgresChunkFileStore.CommandAsync(
            db,
            """INSERT INTO praxy.file_derivative_chunks (derivative_id, "index", data) VALUES (@derivative_id, @index, @data)""",
            ct);
        cmd.Parameters.AddWithValue("derivative_id", derivativeId);
        cmd.Parameters.AddWithValue("index", index);
        cmd.Parameters.AddWithValue("data", data.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Only ever asked to read a whole derivative — Range is a full-file concern and never reaches this store (docs/research/storage.md), so this is deliberately simpler than <c>ChunkReadStream</c> rather than a copy of it.</summary>
internal sealed class DerivativeChunkReadStream(PraxyDb db, Guid derivativeId) : Stream
{
    private NpgsqlCommand? _command;
    private NpgsqlDataReader? _reader;
    private Stream? _chunk;
    private bool _finished;
    private long _position;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return 0;
        while (true)
        {
            if (_finished) return 0;

            if (_reader is null)
            {
                _command = await PostgresChunkFileStore.CommandAsync(
                    db,
                    """
                    SELECT data FROM praxy.file_derivative_chunks
                    WHERE derivative_id = @derivative_id
                    ORDER BY "index"
                    """, ct);
                _command.Parameters.AddWithValue("derivative_id", derivativeId);
                _reader = await _command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            }

            if (_chunk is null)
            {
                if (!await _reader.ReadAsync(ct))
                {
                    _finished = true;
                    return 0;
                }
                _chunk = await _reader.GetStreamAsync(0, ct);
            }

            var read = await _chunk.ReadAsync(buffer, ct);
            if (read > 0)
            {
                _position += read;
                return read;
            }

            await _chunk.DisposeAsync();
            _chunk = null;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask DisposeAsync()
    {
        if (_chunk is not null) await _chunk.DisposeAsync();
        if (_reader is not null) await _reader.DisposeAsync();
        if (_command is not null) await _command.DisposeAsync();
        _chunk = null;
        _reader = null;
        _command = null;
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chunk?.Dispose();
            _reader?.Dispose();
            _command?.Dispose();
            _chunk = null;
            _reader = null;
            _command = null;
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
