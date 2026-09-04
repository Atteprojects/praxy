using Praxy.Storage;

namespace Praxy.Tests.Unit;

/// <summary>
/// The chunk arithmetic behind a Range request. A wrong answer here still streams plausible bytes
/// — the wrong ones — so each boundary case is asserted rather than reasoned about.
/// </summary>
public class ChunkRangeTests
{
    private const int Chunk = 1000;

    [Fact]
    public void The_whole_file_starts_at_chunk_zero_with_no_skip()
    {
        var range = ChunkRange.For(offset: 0, length: 2500, Chunk);
        Assert.Equal(0, range.FirstChunk);
        Assert.Equal(0, range.SkipInFirstChunk);
        Assert.Equal(2, range.LastChunk);
    }

    [Fact]
    public void An_open_ended_read_has_no_last_chunk()
    {
        var range = ChunkRange.For(offset: 0, length: null, Chunk);
        Assert.Equal(0, range.FirstChunk);
        Assert.Equal(0, range.SkipInFirstChunk);
        Assert.Null(range.LastChunk);
    }

    [Fact]
    public void A_range_starting_mid_chunk_skips_into_it()
    {
        // bytes 1500-1999 — the back half of chunk 1, and nothing else.
        var range = ChunkRange.For(offset: 1500, length: 500, Chunk);
        Assert.Equal(1, range.FirstChunk);
        Assert.Equal(500, range.SkipInFirstChunk);
        Assert.Equal(1, range.LastChunk);
    }

    [Fact]
    public void A_range_ending_mid_chunk_still_includes_that_chunk()
    {
        // bytes 500-1499: half of chunk 0 and half of chunk 1. The tail is trimmed by the read's
        // own remaining-byte count, not by leaving the chunk out.
        var range = ChunkRange.For(offset: 500, length: 1000, Chunk);
        Assert.Equal(0, range.FirstChunk);
        Assert.Equal(500, range.SkipInFirstChunk);
        Assert.Equal(1, range.LastChunk);
    }

    [Fact]
    public void A_range_that_is_exactly_one_whole_chunk_names_only_that_chunk()
    {
        var range = ChunkRange.For(offset: 2000, length: 1000, Chunk);
        Assert.Equal(2, range.FirstChunk);
        Assert.Equal(0, range.SkipInFirstChunk);
        Assert.Equal(2, range.LastChunk);
    }

    /// <summary>The off-by-one that matters: an inclusive end exactly on a boundary must not pull in the next chunk.</summary>
    [Fact]
    public void A_range_ending_on_the_last_byte_of_a_chunk_stops_there()
    {
        var range = ChunkRange.For(offset: 0, length: 1000, Chunk);
        Assert.Equal(0, range.LastChunk);

        var oneMore = ChunkRange.For(offset: 0, length: 1001, Chunk);
        Assert.Equal(1, oneMore.LastChunk);
    }

    [Fact]
    public void A_single_byte_read_names_one_chunk()
    {
        var range = ChunkRange.For(offset: 3999, length: 1, Chunk);
        Assert.Equal(3, range.FirstChunk);
        Assert.Equal(999, range.SkipInFirstChunk);
        Assert.Equal(3, range.LastChunk);
    }

    /// <summary>
    /// The reason chunk size is a per-file column: the same byte offset addresses different chunks
    /// under different chunk sizes, so reading it from config would misaddress every file written
    /// before the last retune.
    /// </summary>
    [Fact]
    public void The_same_offset_resolves_differently_under_a_different_chunk_size()
    {
        Assert.Equal(2, ChunkRange.For(5000, 100, 2048).FirstChunk);
        Assert.Equal(904, ChunkRange.For(5000, 100, 2048).SkipInFirstChunk);
        Assert.Equal(0, ChunkRange.For(5000, 100, 524_288).FirstChunk);
        Assert.Equal(5000, ChunkRange.For(5000, 100, 524_288).SkipInFirstChunk);
    }
}
