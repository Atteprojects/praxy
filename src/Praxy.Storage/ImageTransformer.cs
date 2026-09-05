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

        using var source = SKBitmap.Decode(codec) ?? throw Undecodable();
        using var transformed = key.Crop
            ? ResizeAndCrop(source, key.Width, key.Height)
            : source.Resize(new SKImageInfo(key.Width, key.Height), Sampling) ?? throw Undecodable();

        var format = EncodedFormat(key.Format);
        // Quality's sentinel (0, for lossless png) has no meaning to the encoder itself; Skia's own
        // quality parameter is only consulted for lossy formats, so 100 there is inert, not a request
        // for "highest lossy quality" on a format that has none.
        using var encoded = transformed.Encode(format, key.Quality == 0 ? 100 : key.Quality);
        return encoded.ToArray();
    }

    /// <summary>
    /// Scale-to-cover then center-crop: the only way to fill an exact width×height box from a source
    /// of a different aspect ratio without distorting it. Runs in two resizes (cover, then the final
    /// box via <see cref="SKBitmap.ExtractSubset"/>) rather than one, which is the simplest correct
    /// way to get the crop math right at both edges.
    /// </summary>
    private static SKBitmap ResizeAndCrop(SKBitmap source, int targetWidth, int targetHeight)
    {
        var scale = Math.Max((double)targetWidth / source.Width, (double)targetHeight / source.Height);
        var coverWidth = Math.Max(targetWidth, (int)Math.Ceiling(source.Width * scale));
        var coverHeight = Math.Max(targetHeight, (int)Math.Ceiling(source.Height * scale));

        using var covered = source.Resize(new SKImageInfo(coverWidth, coverHeight), Sampling) ?? throw Undecodable();

        var left = (coverWidth - targetWidth) / 2;
        var top = (coverHeight - targetHeight) / 2;
        var cropped = new SKBitmap(targetWidth, targetHeight, covered.ColorType, covered.AlphaType);
        if (!covered.ExtractSubset(cropped, new SKRectI(left, top, left + targetWidth, top + targetHeight)))
        {
            cropped.Dispose();
            throw Undecodable();
        }
        return cropped;
    }

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
