using Praxy.Core.Errors;
using Praxy.Storage;
using SkiaSharp;

namespace Praxy.Tests.Unit;

public class ImageTransformerTests
{
    /// <summary>A real, tiny (10x10) encoded PNG — enough for <c>SKCodec</c> to report genuine header dimensions without needing a crafted decompression-bomb fixture.</summary>
    private static byte[] TinyPng(int width = 10, int height = 10)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Transform_produces_the_requested_pixel_dimensions()
    {
        var transformer = new ImageTransformer(new StorageOptions());
        var key = new DerivativeKey(Width: 20, Height: 20, Format: "png", Quality: 0, Crop: false);

        var encoded = transformer.Transform(TinyPng(), key);

        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data);
        Assert.Equal(20, codec!.Info.Width);
        Assert.Equal(20, codec.Info.Height);
    }

    [Fact]
    public void Same_input_and_key_produce_byte_identical_output()
    {
        var transformer = new ImageTransformer(new StorageOptions());
        var key = new DerivativeKey(Width: 32, Height: 32, Format: "png", Quality: 0, Crop: true);
        var source = TinyPng();

        var first = transformer.Transform(source, key);
        var second = transformer.Transform(source, key);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_source_whose_header_dimensions_exceed_the_pixel_ceiling_is_rejected_before_the_full_decode()
    {
        // A real 10x10 PNG (100 pixels) against a ceiling of 50 — the same check that would reject a
        // 100x100 file whose header claims 30,000x30,000 pixels, exercised without needing a crafted
        // decompression-bomb fixture: the header's claimed dimensions are genuine here, just larger
        // than this test's configured limit.
        var transformer = new ImageTransformer(new StorageOptions(MaxSourceImagePixels: 50));
        var key = new DerivativeKey(Width: 20, Height: 20, Format: "png", Quality: 0, Crop: false);

        var ex = Assert.Throws<PraxyException>(() => transformer.Transform(TinyPng(), key));
        Assert.Equal(ErrorTypes.FileTransformSourceTooLarge, ex.Type);
        Assert.Equal(400, ex.Code);
    }

    [Fact]
    public void Unsupported_bytes_are_rejected_rather_than_crashing()
    {
        var transformer = new ImageTransformer(new StorageOptions());
        var key = new DerivativeKey(Width: 20, Height: 20, Format: "png", Quality: 0, Crop: false);

        var ex = Assert.Throws<PraxyException>(() => transformer.Transform([1, 2, 3, 4], key));
        Assert.Equal(ErrorTypes.FileTransformInvalid, ex.Type);
    }
}
