using System.Security.Cryptography;
using Praxy.Storage;

namespace Praxy.Tests.Unit;

/// <summary>
/// Chunk boundary arithmetic, tested without a database — the off-by-one that matters here is a
/// file that is an exact multiple of the chunk size (must not get an empty trailing chunk) or one
/// byte over (must get a 1-byte final chunk), and neither is visible from a round-trip test that
/// only checks the bytes came back.
/// </summary>
public class ChunkedWriteStreamTests
{
    /// <summary>Collects chunks in memory instead of inserting rows.</summary>
    private sealed class RecordingWriteStream(int chunkSize) : ChunkedWriteStream(chunkSize)
    {
        public List<byte[]> Chunks { get; } = [];

        protected override Task WriteChunkAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            Assert.Equal(Chunks.Count, index); // indexes are dense and in order
            Chunks.Add(data.ToArray());
            return Task.CompletedTask;
        }
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static async Task<RecordingWriteStream> WriteAsync(int chunkSize, byte[] payload, int copyBuffer = 7)
    {
        var stream = new RecordingWriteStream(chunkSize);
        // Written in small, deliberately chunk-misaligned slices: a real upload's reads never line
        // up with the chunk size either.
        for (var offset = 0; offset < payload.Length; offset += copyBuffer)
        {
            var take = Math.Min(copyBuffer, payload.Length - offset);
            await stream.WriteAsync(payload.AsMemory(offset, take));
        }
        await stream.CompleteAsync(CancellationToken.None);
        return stream;
    }

    [Theory]
    [InlineData(0, 0)]           // zero-byte file: no chunks at all
    [InlineData(1, 1)]
    [InlineData(63, 1)]
    [InlineData(64, 1)]          // exact multiple: still one chunk, no empty trailing row
    [InlineData(65, 2)]          // one byte over: a second chunk holding exactly one byte
    [InlineData(128, 2)]         // exact multiple again, two chunks
    [InlineData(129, 3)]
    [InlineData(1000, 16)]
    public async Task Chunk_count_is_exact_at_and_around_every_boundary(int size, int expectedChunks)
    {
        var stream = await WriteAsync(chunkSize: 64, Pattern(size));

        Assert.Equal(expectedChunks, stream.ChunkCount);
        Assert.Equal(expectedChunks, stream.Chunks.Count);
        Assert.Equal(size, stream.BytesWritten);
        Assert.DoesNotContain(stream.Chunks, c => c.Length == 0);
    }

    [Fact]
    public async Task Every_chunk_but_the_last_is_full_and_the_last_holds_the_remainder()
    {
        var stream = await WriteAsync(chunkSize: 64, Pattern(65));

        Assert.Equal(64, stream.Chunks[0].Length);
        Assert.Single(stream.Chunks[1]);
    }

    [Fact]
    public async Task Concatenated_chunks_reproduce_the_input_exactly()
    {
        var payload = Pattern(1000);
        var stream = await WriteAsync(chunkSize: 64, payload);

        Assert.Equal(payload, stream.Chunks.SelectMany(c => c).ToArray());
    }

    [Fact]
    public async Task Checksum_is_the_sha256_of_everything_written()
    {
        var payload = Pattern(1000);
        var stream = await WriteAsync(chunkSize: 64, payload);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), stream.Checksum);
    }

    [Fact]
    public async Task A_zero_byte_file_still_gets_the_sha256_of_the_empty_input()
    {
        var stream = await WriteAsync(chunkSize: 64, []);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), stream.Checksum);
        Assert.Equal(0, stream.BytesWritten);
        Assert.Empty(stream.Chunks);
    }

    [Fact]
    public async Task Chunking_is_independent_of_how_the_source_is_sliced()
    {
        var payload = Pattern(999);
        var oneByteAtATime = await WriteAsync(chunkSize: 64, payload, copyBuffer: 1);
        var allAtOnce = await WriteAsync(chunkSize: 64, payload, copyBuffer: payload.Length);
        var oversized = await WriteAsync(chunkSize: 64, payload, copyBuffer: 500);

        Assert.Equal(oneByteAtATime.Chunks.Count, allAtOnce.Chunks.Count);
        Assert.Equal(oneByteAtATime.Chunks.Count, oversized.Chunks.Count);
        Assert.Equal(oneByteAtATime.Checksum, allAtOnce.Checksum);
        Assert.Equal(oneByteAtATime.Checksum, oversized.Checksum);
    }

    [Fact]
    public void Checksum_is_not_readable_before_completion()
    {
        var stream = new RecordingWriteStream(64);
        Assert.Throws<InvalidOperationException>(() => stream.Checksum);
    }
}
