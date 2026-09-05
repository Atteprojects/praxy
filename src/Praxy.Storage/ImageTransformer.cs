using Praxy.Core.Errors;
using SkiaSharp;

namespace Praxy.Storage;

/// <summary>
/// The one place attacker-supplied bytes actually reach native code. SkiaSharp is C++ under the
/// managed surface (docs/research/storage.md's honest trade: a malformed image here is potentially
/// memory corruption, not just a .NET exception), so every step here exists to bound what a decode
/// can do before it happens rather than after.
/// </summary>
public sealed class ImageTransformer(StorageOptions options)
{
    private static readonly SKSamplingOptions Sampling = SKSamplingOptions.Default;

    /// <summary>
    /// Decodes <paramref name="sourceBytes"/>, resizes/crops to <paramref name="key"/>'s dimensions,
    /// and encodes to its format/quality.
    ///
    /// <para>
    /// <b>Decode limits, before the full decode.</b> <c>SKCodec.Create</c> only parses the header —
    /// it does not allocate a pixel buffer — so the dimensions it reports are checked against
    /// <see cref="StorageOptions.MaxSourceImagePixels"/> *before* <c>SKBitmap.Decode</c> is ever
    /// called. A 100×100 file claiming 30,000×30,000 pixels in its header is rejected at the cheap
    /// step; only a file that passes the check reaches the allocating one.
    /// </para>
    /// </summary>
    public byte[] Transform(byte[] sourceBytes, DerivativeKey key)
    {
        using var data = SKData.CreateCopy(sourceBytes);
        using var codec = SKCodec.Create(data) ?? throw Undecodable();

        var info = codec.Info;
        if ((long)info.Width * info.Height > options.MaxSourceImagePixels)
        {
            throw new PraxyException(400, ErrorTypes.FileTransformSourceTooLarge,
                $"This image's {info.Width}x{info.Height} pixels exceed the " +
                $"{options.MaxSourceImagePixels}-pixel limit for transforms.");
        }

        var decoded = SKBitmap.Decode(codec) ?? throw Undecodable();
        // Corrects for the EXIF orientation tag before anything else touches pixels: a phone photo's
        // bytes are stored however the sensor read them, with the rotation recorded as metadata
        // rather than baked in, so skipping this step transforms sideways or upside down (the bug
        // this exists to fix — see docs/handoff/storage-transform-gravity-prompt.md).
        var source = ApplyOrientation(decoded, codec.EncodedOrigin);
        if (!ReferenceEquals(source, decoded))
            decoded.Dispose();

        try
        {
            var transformed = key.Crop
                ? ResizeAndCrop(source, key.Width, key.Height, key.Gravity)
                : source.Resize(new SKImageInfo(key.Width, key.Height), Sampling) ?? throw Undecodable();

            try
            {
                var format = EncodedFormat(key.Format);
                // JPEG has no alpha channel, so a transparent source needs an explicit background or
                // Skia's own default (black) leaks through — surprising for exactly the
                // transparent-logo case transforms are asked to thumbnail. png/webp both support
                // alpha, so they pass through untouched.
                using var flattened = format == SKEncodedImageFormat.Jpeg ? FlattenOntoWhite(transformed) : null;
                var toEncode = flattened ?? transformed;
                // Quality's sentinel (0, for lossless png) has no meaning to the encoder itself; Skia's
                // own quality parameter is only consulted for lossy formats, so 100 there is inert, not
                // a request for "highest lossy quality" on a format that has none.
                using var encoded = toEncode.Encode(format, key.Quality == 0 ? 100 : key.Quality);
                return encoded.ToArray();
            }
            finally
            {
                transformed.Dispose();
            }
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>
    /// Scale-to-cover then crop at <paramref name="gravity"/>'s anchor: the only way to fill an exact
    /// width×height box from a source of a different aspect ratio without distorting it. Runs in two
    /// resizes (cover, then the final box via <see cref="SKBitmap.ExtractSubset"/>) rather than one,
    /// which is the simplest correct way to get the crop math right at every edge.
    /// </summary>
    private static SKBitmap ResizeAndCrop(SKBitmap source, int targetWidth, int targetHeight, string gravity)
    {
        var scale = Math.Max((double)targetWidth / source.Width, (double)targetHeight / source.Height);
        var coverWidth = Math.Max(targetWidth, (int)Math.Ceiling(source.Width * scale));
        var coverHeight = Math.Max(targetHeight, (int)Math.Ceiling(source.Height * scale));

        using var covered = source.Resize(new SKImageInfo(coverWidth, coverHeight), Sampling) ?? throw Undecodable();

        var (left, top) = ImageTransforms.GravityOffset(gravity, coverWidth, coverHeight, targetWidth, targetHeight);
        var cropped = new SKBitmap(targetWidth, targetHeight, covered.ColorType, covered.AlphaType);
        if (!covered.ExtractSubset(cropped, new SKRectI(left, top, left + targetWidth, top + targetHeight)))
        {
            cropped.Dispose();
            throw Undecodable();
        }
        return cropped;
    }

    /// <summary>
    /// Composites <paramref name="source"/> over an opaque white canvas the same size — the default
    /// flatten for a format with no alpha channel of its own. <see cref="SKCanvas.DrawBitmap"/>'s
    /// default paint blends with the normal source-over rule, so a fully transparent pixel becomes
    /// pure white and a partially transparent one blends proportionally, exactly like flattening in
    /// an image editor.
    /// </summary>
    private static SKBitmap FlattenOntoWhite(SKBitmap source)
    {
        var flattened = new SKBitmap(source.Width, source.Height, SKColorType.Rgb888x, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(flattened);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0, Sampling);
        return flattened;
    }

    /// <summary>
    /// Undoes the EXIF orientation tag by drawing <paramref name="source"/> through the matrix that
    /// maps its stored pixel layout back to upright, into a freshly sized bitmap (swapped
    /// width/height for the four orientations that are actually a 90°/270° rotation). Returns
    /// <paramref name="source"/> itself, unchanged, for <see cref="SKEncodedOrigin.TopLeft"/> — the
    /// overwhelming common case (no EXIF tag, or an already-upright one) — so the normal path costs
    /// nothing beyond the one enum comparison.
    /// </summary>
    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return source;

        var (destWidth, destHeight) = SwapsDimensions(origin)
            ? (source.Height, source.Width)
            : (source.Width, source.Height);

        var oriented = new SKBitmap(destWidth, destHeight, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(oriented);
        canvas.SetMatrix(OrientationMatrix(origin, source.Width, source.Height));
        canvas.DrawBitmap(source, 0, 0, Sampling);
        return oriented;
    }

    /// <summary>
    /// True for the four EXIF origins that are a 90°/270° rotation rather than a flip/half-turn — the
    /// destination bitmap (and, in <see cref="Praxy.Storage.DerivativesService"/>, the source
    /// dimensions cached for aspect-ratio derivation) needs width and height swapped for exactly
    /// these four.
    /// </summary>
    public static bool SwapsDimensions(SKEncodedOrigin origin) => origin is
        SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or
        SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// One affine matrix per non-identity EXIF origin, each derived directly from where a source
    /// pixel at (x, y) must land — e.g. <see cref="SKEncodedOrigin.RightTop"/> (a 90° clockwise
    /// rotation, EXIF tag 6) sends (x, y) to (height − y, x), which is exactly what
    /// <c>ScaleX:0, SkewX:-1, TransX:height, SkewY:1, ScaleY:0, TransY:0</c> encodes. Verified against
    /// SkiaSharp 4.151.2's real <c>SKMatrix</c> constructor order (scaleX, skewX, transX, skewY,
    /// scaleY, transY, persp0, persp1, persp2) rather than assumed from memory.
    /// </summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.CreateIdentity(),
    };

    private static SKEncodedImageFormat EncodedFormat(string format) => format switch
    {
        "png" => SKEncodedImageFormat.Png,
        "jpeg" => SKEncodedImageFormat.Jpeg,
        "webp" => SKEncodedImageFormat.Webp,
        _ => throw Undecodable(),
    };

    private static PraxyException Undecodable() =>
        new(400, ErrorTypes.FileTransformInvalid, "This file could not be decoded as an image.");
}
