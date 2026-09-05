using Praxy.Core.Errors;

namespace Praxy.Storage;

/// <summary>What a caller asked for on the download endpoint's <c>?width=/?height=/?format=/?quality=</c>. Raw, unvalidated — <see cref="ImageTransforms.Resolve"/> is where these become a <see cref="DerivativeKey"/> or a rejection.</summary>
public readonly record struct TransformRequest(int? Width, int? Height, string? Format, int? Quality)
{
    /// <summary>With none present the endpoint behaves exactly as it does today, including Range — a derivative is never generated for a plain download.</summary>
    public bool IsRequested => Width is not null || Height is not null || Format is not null || Quality is not null;
}

/// <summary>
/// A resolved, ladder-snapped transform: the exact identity <c>file_derivatives</c> keys on
/// (<c>file_id</c> is the caller's job to add). Two requests that resolve to the same key must
/// produce byte-identical output, which is what makes the cache-hit test in the prompt assertable.
/// </summary>
/// <summary>
/// <paramref name="Quality"/> is <c>0</c>, never <c>null</c>, when the format is lossless (<c>png</c>)
/// and quality has no meaning — a real, storable sentinel rather than a nullable column, because
/// Postgres treats every <c>NULL</c> in a unique index as distinct from every other, which would
/// silently defeat the <c>(file_id, width, height, format, quality)</c> uniqueness this key exists to
/// provide for exactly the format that needs it least dropped.
/// </summary>
public readonly record struct DerivativeKey(int Width, int Height, string Format, int Quality, bool Crop)
{
    public string MimeType => ImageTransforms.MimeTypeFor(Format);
}

/// <summary>
/// Turns a raw <see cref="TransformRequest"/> plus the source image's own dimensions/type into a
/// <see cref="DerivativeKey"/> — or rejects it. Deliberately a pure function of its inputs, no
/// database and no SkiaSharp: the ladder snapping is the security property
/// (docs/research/storage.md), and it has to be testable in all four directions (below the first
/// rung, exactly on one, between two, above the top) without standing up either.
///
/// <para>
/// <b>Bounded key space, not just bounded dimensions.</b> When only one of width/height is given,
/// the other is derived from the source's own (fixed, non-attacker-controlled) aspect ratio rather
/// than independently snapped — so varying just <c>?width=</c> against one file still produces at
/// most 6 distinct rows, the same bound a naive reading of the ladder alone would only give the
/// two-dimension case. <c>quality</c> is not snapped to a ladder of its own: it is already bounded to
/// a fixed range (1-100), so it cannot be walked to create an unbounded number of rows the way a raw
/// pixel dimension could before this rule existed.
/// </para>
/// </summary>
public static class ImageTransforms
{
    /// <summary>
    /// Decodable source types. Deliberately narrower than <see cref="InlineTypes.Safe"/>: animated
    /// GIF (frame semantics nobody asked for) and formats this pipeline hasn't been exercised
    /// against are left out rather than assumed to "just work" against attacker-supplied bytes
    /// (docs/research/storage.md's "Deliberately out of scope").
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedSourceTypes = ["image/png", "image/jpeg", "image/webp"];

    private static readonly IReadOnlyDictionary<string, string> FormatMimeTypes = new Dictionary<string, string>
    {
        ["png"] = "image/png",
        ["jpeg"] = "image/jpeg",
        ["webp"] = "image/webp",
    };

    public const int DefaultQuality = 82;

    public static DerivativeKey Resolve(TransformRequest request, string sourceMimeType, int sourceWidth, int sourceHeight)
    {
        EnsureSupportedSourceType(sourceMimeType);

        var format = NormalizeFormat(request.Format, sourceMimeType);
        var quality = NormalizeQuality(request.Quality, format);
        var (width, height, crop) = ResolveDimensions(request.Width, request.Height, sourceWidth, sourceHeight);
        return new DerivativeKey(width, height, format, quality, crop);
    }

    public static string MimeTypeFor(string format) => FormatMimeTypes[format];

    /// <summary>
    /// Shared by <see cref="Resolve"/> and <c>DerivativesService</c>'s own dimension probe, so an
    /// unsupported source type is rejected the same way and with the same message whichever call
    /// site notices it first — one before any decode, the other before the (rarer, one-time-per-file)
    /// header probe that only some request shapes need.
    /// </summary>
    public static void EnsureSupportedSourceType(string sourceMimeType)
    {
        if (!SupportedSourceTypes.Contains(sourceMimeType, StringComparer.OrdinalIgnoreCase))
        {
            throw Invalid(
                $"'{sourceMimeType}' cannot be transformed. Supported source types: " +
                $"{string.Join(", ", SupportedSourceTypes)}.");
        }
    }

    private static (int Width, int Height, bool Crop) ResolveDimensions(
        int? width, int? height, int sourceWidth, int sourceHeight)
    {
        // Neither requested: the source's own dimensions. Not attacker-controlled per request — the
        // file has exactly one native size — so this branch alone can never grow the key space.
        if (width is null && height is null)
            return (sourceWidth, sourceHeight, false);

        if (width is { } w && height is { } h)
        {
            var snappedWidth = SnapUpOrThrow(w, "width");
            var snappedHeight = SnapUpOrThrow(h, "height");
            return (snappedWidth, snappedHeight, true);
        }

        if (width is { } widthOnly)
        {
            var snapped = SnapUpOrThrow(widthOnly, "width");
            return (snapped, DerivedDimension(snapped, sourceWidth, sourceHeight), false);
        }

        var heightOnly = height!.Value;
        var snappedHeight2 = SnapUpOrThrow(heightOnly, "height");
        return (DerivedDimension(snappedHeight2, sourceHeight, sourceWidth), snappedHeight2, false);
    }

    /// <summary>The other axis, scaled to preserve the source's aspect ratio — never independently snapped, since it isn't what the caller asked for.</summary>
    private static int DerivedDimension(int snappedRequested, int sourceRequestedAxis, int sourceOtherAxis) =>
        Math.Max(1, (int)Math.Round(sourceOtherAxis * (snappedRequested / (double)sourceRequestedAxis)));

    private static int SnapUpOrThrow(int requested, string axis)
    {
        if (requested < 1)
            throw Invalid($"{axis} must be at least 1 pixel.");
        return DimensionLadder.SnapUp(requested)
            ?? throw Invalid($"{axis}={requested} exceeds the maximum of {DimensionLadder.TopRung} pixels.");
    }

    private static string NormalizeFormat(string? format, string sourceMimeType)
    {
        if (format is null)
        {
            return sourceMimeType.ToLowerInvariant() switch
            {
                "image/png" => "png",
                "image/jpeg" => "jpeg",
                "image/webp" => "webp",
                _ => "png",
            };
        }

        var normalized = format.Trim().ToLowerInvariant();
        if (normalized == "jpg") normalized = "jpeg";
        if (!FormatMimeTypes.ContainsKey(normalized))
        {
            throw Invalid(
                $"Unsupported format '{format}'. Supported: {string.Join(", ", FormatMimeTypes.Keys)}.");
        }
        return normalized;
    }

    /// <summary>
    /// <c>0</c> for <c>png</c>: it is lossless, quality has no meaning, and dropping it to a fixed
    /// sentinel (see <see cref="DerivativeKey"/>) keeps the cache key from fragmenting into duplicate
    /// rows with identical bytes for every quality value a caller happens to pass alongside
    /// <c>format=png</c>.
    /// </summary>
    private static int NormalizeQuality(int? quality, string format)
    {
        if (format == "png") return 0;
        if (quality is null) return DefaultQuality;
        if (quality is < 1 or > 100)
            throw Invalid("quality must be between 1 and 100.");
        return quality.Value;
    }

    private static PraxyException Invalid(string message) => new(400, ErrorTypes.FileTransformInvalid, message);
}
