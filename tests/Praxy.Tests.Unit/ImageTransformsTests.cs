using Praxy.Core.Errors;
using Praxy.Storage;

namespace Praxy.Tests.Unit;

public class ImageTransformsTests
{
    private const string Png = "image/png";
    private const string Jpeg = "image/jpeg";

    [Fact]
    public void Same_request_against_the_same_source_resolves_to_the_same_key()
    {
        var request = new TransformRequest(200, null, null, null);
        var a = ImageTransforms.Resolve(request, Jpeg, sourceWidth: 4000, sourceHeight: 3000);
        var b = ImageTransforms.Resolve(request, Jpeg, sourceWidth: 4000, sourceHeight: 3000);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Both_dimensions_given_snap_independently_and_crop()
    {
        var key = ImageTransforms.Resolve(new TransformRequest(200, 300, null, null), Jpeg, 4000, 3000);
        Assert.Equal(256, key.Width);
        Assert.Equal(512, key.Height);
        Assert.True(key.Crop);
    }

    [Fact]
    public void Only_width_given_derives_height_from_source_aspect_ratio_without_cropping()
    {
        // Source is 2:1 (4000x2000). Requested width 200 snaps to 256, so height should scale to 128.
        var key = ImageTransforms.Resolve(new TransformRequest(200, null, null, null), Jpeg, 4000, 2000);
        Assert.Equal(256, key.Width);
        Assert.Equal(128, key.Height);
        Assert.False(key.Crop);
    }

    [Fact]
    public void Only_height_given_derives_width_from_source_aspect_ratio_without_cropping()
    {
        var key = ImageTransforms.Resolve(new TransformRequest(null, 200, null, null), Jpeg, 4000, 2000);
        Assert.Equal(256, key.Height);
        Assert.Equal(512, key.Width);
        Assert.False(key.Crop);
    }

    [Fact]
    public void Neither_dimension_given_keeps_the_sources_own_size()
    {
        var key = ImageTransforms.Resolve(new TransformRequest(null, null, "webp", null), Jpeg, 800, 600);
        Assert.Equal(800, key.Width);
        Assert.Equal(600, key.Height);
        Assert.False(key.Crop);
    }

    [Fact]
    public void Above_the_top_rung_is_rejected()
    {
        var ex = Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(2049, null, null, null), Jpeg, 4000, 3000));
        Assert.Equal(ErrorTypes.FileTransformInvalid, ex.Type);
    }

    [Fact]
    public void Zero_or_negative_dimensions_are_rejected()
    {
        Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(0, null, null, null), Jpeg, 100, 100));
        Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(-5, null, null, null), Jpeg, 100, 100));
    }

    [Fact]
    public void Unsupported_source_type_is_rejected_before_any_dimension_work()
    {
        var ex = Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(200, null, null, null), "application/pdf", 100, 100));
        Assert.Equal(ErrorTypes.FileTransformInvalid, ex.Type);
        Assert.Equal(400, ex.Code);
    }

    [Fact]
    public void Format_defaults_to_the_sources_own_type()
    {
        Assert.Equal("jpeg", ImageTransforms.Resolve(new TransformRequest(200, null, null, null), Jpeg, 100, 100).Format);
        Assert.Equal("png", ImageTransforms.Resolve(new TransformRequest(200, null, null, null), Png, 100, 100).Format);
    }

    [Fact]
    public void Jpg_is_accepted_as_an_alias_for_jpeg()
    {
        Assert.Equal("jpeg", ImageTransforms.Resolve(new TransformRequest(200, null, "jpg", null), Jpeg, 100, 100).Format);
    }

    [Fact]
    public void Unsupported_format_is_rejected()
    {
        Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(200, null, "bmp", null), Jpeg, 100, 100));
    }

    [Fact]
    public void Png_quality_is_normalized_to_the_zero_sentinel_regardless_of_what_was_requested()
    {
        var key = ImageTransforms.Resolve(new TransformRequest(200, null, "png", 90), Jpeg, 100, 100);
        Assert.Equal(0, key.Quality);
    }

    [Fact]
    public void Jpeg_quality_defaults_when_omitted_and_is_validated_when_given()
    {
        Assert.Equal(ImageTransforms.DefaultQuality,
            ImageTransforms.Resolve(new TransformRequest(200, null, "jpeg", null), Jpeg, 100, 100).Quality);
        Assert.Equal(50, ImageTransforms.Resolve(new TransformRequest(200, null, "jpeg", 50), Jpeg, 100, 100).Quality);
        Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(200, null, "jpeg", 0), Jpeg, 100, 100));
        Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(200, null, "jpeg", 101), Jpeg, 100, 100));
    }
}
