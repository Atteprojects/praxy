using Praxy.Storage;

namespace Praxy.Tests.Unit;

/// <summary>
/// Range parsing, which is standard and easy to half-do: the forms clients actually send are
/// suffix (<c>bytes=-500</c>), open-ended (<c>bytes=500-</c>) and closed, and getting any of them
/// off by one byte produces a file that looks right and plays wrong.
/// </summary>
public class ByteRangesTests
{
    private const long Total = 1000;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_header_is_a_full_response(string? header)
    {
        Assert.Equal(ByteRangeOutcome.Full, ByteRanges.Parse(header, Total).Outcome);
    }

    [Fact]
    public void A_closed_range_is_inclusive_at_both_ends()
    {
        var range = ByteRanges.Parse("bytes=0-99", Total);
        Assert.Equal(ByteRangeOutcome.Partial, range.Outcome);
        Assert.Equal(0, range.Start);
        Assert.Equal(99, range.End);
        // Content-Length on a 206 is the part, not the file — 100 bytes, not 1000.
        Assert.Equal(100, range.Length);
    }

    [Fact]
    public void An_open_ended_range_runs_to_the_last_byte()
    {
        var range = ByteRanges.Parse("bytes=500-", Total);
        Assert.Equal(ByteRangeOutcome.Partial, range.Outcome);
        Assert.Equal(500, range.Start);
        Assert.Equal(999, range.End);
        Assert.Equal(500, range.Length);
    }

    [Fact]
    public void A_suffix_range_counts_back_from_the_end()
    {
        var range = ByteRanges.Parse("bytes=-500", Total);
        Assert.Equal(ByteRangeOutcome.Partial, range.Outcome);
        Assert.Equal(500, range.Start);
        Assert.Equal(999, range.End);
    }

    /// <summary>Asking for more trailing bytes than exist is legal, and means the whole file.</summary>
    [Fact]
    public void A_suffix_larger_than_the_file_is_the_whole_file()
    {
        var range = ByteRanges.Parse("bytes=-5000", Total);
        Assert.Equal(ByteRangeOutcome.Partial, range.Outcome);
        Assert.Equal(0, range.Start);
        Assert.Equal(999, range.End);
    }

    [Fact]
    public void An_end_past_the_file_is_clamped_rather_than_rejected()
    {
        var range = ByteRanges.Parse("bytes=900-99999", Total);
        Assert.Equal(ByteRangeOutcome.Partial, range.Outcome);
        Assert.Equal(900, range.Start);
        Assert.Equal(999, range.End);
    }

    [Fact]
    public void The_last_single_byte_is_satisfiable()
    {
        var range = ByteRanges.Parse("bytes=999-999", Total);
        Assert.Equal(ByteRangeOutcome.Partial, range.Outcome);
        Assert.Equal(1, range.Length);
    }

    [Theory]
    [InlineData("bytes=1000-")]        // first byte is exactly past the end
    [InlineData("bytes=1000-1099")]
    [InlineData("bytes=99999-")]
    [InlineData("bytes=-0")]           // "the last zero bytes" — nothing to return
    public void A_range_outside_the_file_is_unsatisfiable(string header)
    {
        Assert.Equal(ByteRangeOutcome.Unsatisfiable, ByteRanges.Parse(header, Total).Outcome);
    }

    /// <summary>RFC 9110: a zero-length representation cannot satisfy any byte range.</summary>
    [Fact]
    public void Every_range_over_an_empty_file_is_unsatisfiable()
    {
        Assert.Equal(ByteRangeOutcome.Unsatisfiable, ByteRanges.Parse("bytes=0-", 0).Outcome);
        Assert.Equal(ByteRangeOutcome.Unsatisfiable, ByteRanges.Parse("bytes=-10", 0).Outcome);
        // …but a request with no Range header for an empty file is still a normal 200.
        Assert.Equal(ByteRangeOutcome.Full, ByteRanges.Parse(null, 0).Outcome);
    }

    /// <summary>
    /// Multi-range is answered with the whole file rather than <c>multipart/byteranges</c> — the
    /// spec permits ignoring a Range header, and no browser needs multipart for media playback.
    /// </summary>
    [Theory]
    [InlineData("bytes=0-99,200-299")]
    [InlineData("bytes=0-99, 200-299, 400-499")]
    public void A_multi_range_request_falls_back_to_the_full_body(string header)
    {
        Assert.Equal(ByteRangeOutcome.Full, ByteRanges.Parse(header, Total).Outcome);
    }

    [Theory]
    [InlineData("items=0-99")]     // unknown unit
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=")]
    [InlineData("bytes=100")]      // no dash at all
    [InlineData("bytes=200-100")]  // last before first: an invalid spec, so the header is ignored
    [InlineData("bytes=-abc")]
    [InlineData("0-99")]
    public void Anything_malformed_is_ignored_rather_than_rejected(string header)
    {
        Assert.Equal(ByteRangeOutcome.Full, ByteRanges.Parse(header, Total).Outcome);
    }

    [Fact]
    public void The_unit_is_matched_case_insensitively_and_whitespace_tolerantly()
    {
        Assert.Equal(ByteRangeOutcome.Partial, ByteRanges.Parse("BYTES=0-9", Total).Outcome);
        Assert.Equal(ByteRangeOutcome.Partial, ByteRanges.Parse("  bytes= 0 - 9 ", Total).Outcome);
    }
}
