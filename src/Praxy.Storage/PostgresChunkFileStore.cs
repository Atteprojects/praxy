using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Praxy.Persistence;

namespace Praxy.Storage;

/// <summary>
/// The only <see cref="IFileStore"/> implementation in v1: bytes split across
/// <c>praxy.file_chunks</c> rows. Raw Npgsql over <see cref="PraxyDb"/>'s own connection — the same
/// "raw Npgsql through the EF connection" pattern <c>SchemaDdl</c>/<c>RowsService</c> already use —
/// so chunk writes join the caller's ambient EF transaction and commit or roll back with the
/// metadata row. That is what makes a failed upload leave nothing behind rather than a half-file.
/// </summary>
public sealed class PostgresChunkFileStore(PraxyDb db) : IFileStore
{
    public FileWriteStream OpenWrite(Guid fileId, int chunkSizeBytes) =>
        new ChunkWriteStream(db, fileId, chunkSizeBytes);

    public Stream OpenRead(Guid fileId, int chunkSizeBytes, long offset = 0, long? length = null) =>
        new ChunkReadStream(db, fileId, chunkSizeBytes, offset, length);

    public async Task DeleteAsync(Guid fileId, CancellationToken ct)
    {
        await using var cmd = await CommandAsync(db, "DELETE FROM praxy.file_chunks WHERE file_id = @file_id", ct);
        cmd.Parameters.AddWithValue("file_id", fileId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// One command on the context's connection, enlisted in whatever transaction the caller already
    /// opened. Every statement this store issues goes through here so the ambient-transaction
    /// enlistment can't be forgotten in one place and remembered in another.
    /// </summary>
    internal static async Task<NpgsqlCommand> CommandAsync(PraxyDb db, string sql, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);
        var cmd = new NpgsqlCommand(sql, conn);
        if (db.Database.CurrentTransaction is { } tx)
            cmd.Transaction = (NpgsqlTransaction)tx.GetDbTransaction();
        return cmd;
    }
}

/// <summary>Inserts each chunk as a row on the caller's ambient transaction. All the buffering and boundary arithmetic is <see cref="ChunkedWriteStream"/>'s.</summary>
internal sealed class ChunkWriteStream(PraxyDb db, Guid fileId, int chunkSizeBytes)
    : ChunkedWriteStream(chunkSizeBytes)
{
    protected override async Task WriteChunkAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await using var cmd = await PostgresChunkFileStore.CommandAsync(
            db, """INSERT INTO praxy.file_chunks (file_id, "index", data) VALUES (@file_id, @index, @data)""", ct);
        cmd.Parameters.AddWithValue("file_id", fileId);
        cmd.Parameters.AddWithValue("index", index);
        // A copy, not the caller's buffer: it is reused for the next chunk, and the parameter is
        // read when the command executes.
        cmd.Parameters.AddWithValue("data", data.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Streams chunks back in order from one cursor: a single ordered read, with Npgsql in
/// <see cref="CommandBehavior.SequentialAccess"/> mode so each <c>bytea</c> value is pulled off the
/// wire as the caller consumes it instead of being materialized per row.
///
/// <para>
/// A range narrows that cursor rather than being skipped over afterwards. The chunk layout makes
/// the arithmetic exact — and it uses the file's <b>own</b> recorded chunk size, never the
/// configured default, so retuning <c>Praxy:Storage:ChunkSizeBytes</c> can't misaddress a byte of
/// anything already stored:
/// </para>
/// <code>
/// firstChunk  = offset / chunkSize          lastChunk = (offset + length - 1) / chunkSize
/// skipInFirst = offset % chunkSize
/// </code>
/// <para>
/// The leading partial chunk is trimmed by Postgres (<c>substr</c>) rather than read and discarded
/// here: with the column at <c>STORAGE EXTERNAL</c> that skips the TOAST slices outright. The
/// trailing one is trimmed by <c>_remaining</c> as the caller reads, so a range never returns more
/// bytes than it asked for even though its last chunk usually contains more.
/// </para>
/// </summary>
internal sealed class ChunkReadStream(PraxyDb db, Guid fileId, int chunkSizeBytes, long offset, long? length)
    : Stream
{
    private NpgsqlCommand? _command;
    private NpgsqlDataReader? _reader;
    private Stream? _chunk;
    private bool _finished;
    private long _position;
    private long _remaining = length ?? long.MaxValue;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return 0;
        while (true)
        {
            if (_finished || _remaining <= 0) return 0;

            if (_reader is null)
            {
                var chunks = ChunkRange.For(offset, length, chunkSizeBytes);
                _command = await PostgresChunkFileStore.CommandAsync(
                    db,
                    """
                    SELECT CASE WHEN "index" = @first THEN substr(data, @skip) ELSE data END
                    FROM praxy.file_chunks
                    WHERE file_id = @file_id AND "index" >= @first AND (@last < 0 OR "index" <= @last)
                    ORDER BY "index"
                    """, ct);
                _command.Parameters.AddWithValue("file_id", fileId);
                _command.Parameters.AddWithValue("first", chunks.FirstChunk);
                // substr is 1-based, so a zero skip is `from 1` — the whole chunk.
                _command.Parameters.AddWithValue("skip", chunks.SkipInFirstChunk + 1);
                // -1 stands for "no upper bound", so an open-ended read is the same one statement.
                _command.Parameters.AddWithValue("last", chunks.LastChunk ?? -1);
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

            // Never hand back more than the range asked for: the last chunk of a range usually
            // runs past its end.
            var wanted = (int)Math.Min(buffer.Length, _remaining);
            var read = await _chunk.ReadAsync(buffer[..wanted], ct);
            if (read > 0)
            {
                _position += read;
                _remaining -= read;
                return read;
            }

            // Chunk exhausted — advance to the next row and loop.
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
