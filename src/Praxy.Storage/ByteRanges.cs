namespace Praxy.Storage;

/// <summary>What a <c>Range</c> header asked for, once resolved against the file's real size.</summary>
public enum ByteRangeOutcome
{
    /// <summary>Serve the whole file with <c>200</c>. Covers "no Range header" and every header this server chooses to ignore, which the spec explicitly permits.</summary>
    Full,

    /// <summary>Serve <see cref="ByteRangeRequest.Start"/>..<see cref="ByteRangeRequest.End"/> with <c>206</c>.</summary>
    Partial,

    /// <summary>Nothing to serve: <c>416</c> with <c>Content-Range: bytes */total</c>.</summary>
    Unsatisfiable,
}

/// <summary>One resolved range. <see cref="Start"/>/<see cref="End"/> are inclusive byte offsets, as on the wire.</summary>
public readonly record struct ByteRangeRequest(ByteRangeOutcome Outcome, long Start, long End)
{
    /// <summary>Bytes in the part — which is what <c>Content-Length</c> must report on a <c>206</c>, not the file's size.</summary>
    public long Length => End - Start + 1;
}

/// <summary>
/// <c>Range</c> header parsing, kept a pure function of (header, total size) so every branch is
/// unit-testable without a request.
///
/// <para>
/// Two deliberate simplifications, both allowed by RFC 9110 §14.2 ("a server MAY ignore the Range
/// header field"): a <b>multi-range</b> request (<c>bytes=0-99,200-299</c>) is answered with the
/// full 200 body rather than <c>multipart/byteranges</c> — a multipart encoder is a lot of surface
/// for a case no browser needs for media playback — and anything malformed or in an unknown unit is
/// ignored the same way rather than being an error.
/// </para>
/// </summary>
public static class ByteRanges
{
    private const string Unit = "bytes=";

    public static ByteRangeRequest Parse(string? header, long totalBytes)
    {
        if (string.IsNullOrWhiteSpace(header))
            return Full(totalBytes);

        var value = header.Trim();
        if (!value.StartsWith(Unit, StringComparison.OrdinalIgnoreCase))
            return Full(totalBytes); // unknown unit — ignored, not rejected
        var spec = value[Unit.Length..];

        // Multi-range: answered with the whole file (see the type's remarks).
        if (spec.Contains(','))
            return Full(totalBytes);

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return Full(totalBytes);

        var fromText = spec[..dash].Trim();
        var toText = spec[(dash + 1)..].Trim();

        // Suffix form: bytes=-500 — the *last* 500 bytes, and legitimately larger than the file.
        if (fromText.Length == 0)
        {
            if (!long.TryParse(toText, out var suffix) || suffix < 0)
                return Full(totalBytes);
            if (suffix == 0 || totalBytes == 0)
                return Unsatisfiable();
            var start = Math.Max(0, totalBytes - suffix);
            return new ByteRangeRequest(ByteRangeOutcome.Partial, start, totalBytes - 1);
        }

        if (!long.TryParse(fromText, out var first) || first < 0)
            return Full(totalBytes);

        long last;
        if (toText.Length == 0)
        {
            last = totalBytes - 1; // open-ended: bytes=500-
        }
        else
        {
            if (!long.TryParse(toText, out last) || last < 0)
                return Full(totalBytes);
            // last < first is an invalid spec, and an invalid spec means the header is ignored.
            if (last < first)
                return Full(totalBytes);
            last = Math.Min(last, totalBytes - 1);
        }

        // Past the end (and every range over a zero-length file) is the one case that is an error
        // rather than something to ignore: 416 tells the client its offset is wrong, where a silent
        // 200 would look like a successful seek to the wrong place.
        if (totalBytes == 0 || first > totalBytes - 1)
            return Unsatisfiable();

        return new ByteRangeRequest(ByteRangeOutcome.Partial, first, last);
    }

    private static ByteRangeRequest Full(long totalBytes) =>
        new(ByteRangeOutcome.Full, 0, Math.Max(0, totalBytes - 1));

    private static ByteRangeRequest Unsatisfiable() => new(ByteRangeOutcome.Unsatisfiable, 0, -1);
}
