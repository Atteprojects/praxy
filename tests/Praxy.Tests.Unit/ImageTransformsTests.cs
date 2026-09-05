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

    [Fact]
    public void Gravity_defaults_to_center()
    {
        var key = ImageTransforms.Resolve(new TransformRequest(200, 300, null, null), Jpeg, 4000, 3000);
        Assert.Equal("center", key.Gravity);
    }

    [Fact]
    public void Gravity_is_kept_when_the_request_actually_crops()
    {
        var key = ImageTransforms.Resolve(new TransformRequest(200, 300, null, null, "top"), Jpeg, 4000, 3000);
        Assert.True(key.Crop);
        Assert.Equal("top", key.Gravity);
    }

    [Fact]
    public void Gravity_is_normalized_to_center_when_there_is_no_crop_to_anchor()
    {
        // Only one axis given (or neither) — no crop, so a caller's gravity is inert and must not
        // fragment the cache the way an honoured one legitimately would.
        var widthOnly = ImageTransforms.Resolve(new TransformRequest(200, null, null, null, "top"), Jpeg, 4000, 2000);
        Assert.False(widthOnly.Crop);
        Assert.Equal("center", widthOnly.Gravity);

        var heightOnly = ImageTransforms.Resolve(new TransformRequest(null, 200, null, null, "bottom-right"), Jpeg, 4000, 2000);
        Assert.False(heightOnly.Crop);
        Assert.Equal("center", heightOnly.Gravity);

        var neither = ImageTransforms.Resolve(new TransformRequest(null, null, null, null, "left"), Jpeg, 800, 600);
        Assert.False(neither.Crop);
        Assert.Equal("center", neither.Gravity);
    }

    [Fact]
    public void Different_gravity_on_an_otherwise_identical_crop_is_a_different_key()
    {
        var top = ImageTransforms.Resolve(new TransformRequest(200, 300, null, null, "top"), Jpeg, 4000, 3000);
        var bottom = ImageTransforms.Resolve(new TransformRequest(200, 300, null, null, "bottom"), Jpeg, 4000, 3000);
        Assert.NotEqual(top, bottom);
        Assert.Equal(top with { Gravity = "bottom" }, bottom);
    }

    [Fact]
    public void Unsupported_gravity_is_rejected_even_on_a_request_that_would_not_crop()
    {
        // Validated unconditionally: a typo shouldn't be silently swallowed just because this
        // particular request happens not to need a crop.
        var ex = Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(200, null, null, null, "strat"), Jpeg, 4000, 2000));
        Assert.Equal(ErrorTypes.FileTransformInvalid, ex.Type);
        Assert.Equal(400, ex.Code);
    }

    [Theory]
    // Wider-than-target source (scaled cover is 300x100 against a 100x100 target): all horizontal
    // leftover (200px), no vertical leftover.
    [InlineData("center", 100, 0, 100, 100)]
    [InlineData("top-left", 0, 0, 100, 100)]
    [InlineData("top", 100, 0, 100, 100)]
    [InlineData("top-right", 200, 0, 100, 100)]
    [InlineData("left", 0, 0, 100, 100)]
    [InlineData("right", 200, 0, 100, 100)]
    [InlineData("bottom-left", 0, 0, 100, 100)]
    [InlineData("bottom", 100, 0, 100, 100)]
    [InlineData("bottom-right", 200, 0, 100, 100)]
    public void Gravity_offset_against_a_wider_than_target_source(
        string gravity, int expectedLeft, int expectedTop, int targetWidth, int targetHeight)
    {
        var (left, top) = ImageTransforms.GravityOffset(gravity, coverWidth: 300, coverHeight: 100, targetWidth, targetHeight);
        Assert.Equal(expectedLeft, left);
        Assert.Equal(expectedTop, top);
    }

    [Theory]
    // Taller-than-target source (scaled cover is 100x300 against a 100x100 target): all vertical
    // leftover (200px), no horizontal leftover.
    [InlineData("center", 0, 100, 100, 100)]
    [InlineData("top-left", 0, 0, 100, 100)]
    [InlineData("top", 0, 0, 100, 100)]
    [InlineData("top-right", 0, 0, 100, 100)]
    [InlineData("left", 0, 100, 100, 100)]
    [InlineData("right", 0, 100, 100, 100)]
    [InlineData("bottom-left", 0, 200, 100, 100)]
    [InlineData("bottom", 0, 200, 100, 100)]
    [InlineData("bottom-right", 0, 200, 100, 100)]
    public void Gravity_offset_against_a_taller_than_target_source(
        string gravity, int expectedLeft, int expectedTop, int targetWidth, int targetHeight)
    {
        var (left, top) = ImageTransforms.GravityOffset(gravity, coverWidth: 100, coverHeight: 300, targetWidth, targetHeight);
        Assert.Equal(expectedLeft, left);
        Assert.Equal(expectedTop, top);
    }

    // ---- background (caller-settable, deliberately uncached) ----------------------------------

    [Theory]
    [InlineData("ffffff")]
    [InlineData("000000")]
    [InlineData("1A2b3C")]
    public void A_valid_hex_background_is_kept_and_lowercased(string hex)
    {
        var key = ImageTransforms.Resolve(
            new TransformRequest(256, null, "jpeg", null, null, hex), "image/png", 800, 600);
        Assert.Equal(hex.ToLowerInvariant(), key.Background);
    }

    [Theory]
    [InlineData("#ffffff")]   // '#' would need percent-encoding; accepting both spellings would make two requests for one colour
    [InlineData("fff")]       // shorthand deliberately not accepted
    [InlineData("gggggg")]
    [InlineData("ffffff00")]
    public void A_malformed_background_is_a_clean_400(string hex)
    {
        var ex = Assert.Throws<PraxyException>(() => ImageTransforms.Resolve(
            new TransformRequest(256, null, "jpeg", null, null, hex), "image/png", 800, 600));
        Assert.Equal(400, ex.Code);
        Assert.Equal(ErrorTypes.FileTransformInvalid, ex.Type);
    }

    /// <summary>
    /// png and webp carry their own alpha, so a background changes nothing about the output — keeping
    /// it would make the request uncacheable for no benefit. Same normalization gravity already gets
    /// when it cannot have an effect.
    /// </summary>
    [Theory]
    [InlineData("png")]
    [InlineData("webp")]
    public void A_background_is_dropped_for_a_format_that_keeps_alpha(string format)
    {
        var key = ImageTransforms.Resolve(
            new TransformRequest(256, null, format, null, null, "ff0000"), "image/png", 800, 600);
        Assert.Null(key.Background);
        Assert.True(key.IsCacheable);
    }

    /// <summary>
    /// The security property this parameter is shaped around: a colour has 16.7M values, so it can
    /// never join the stored key the way the ladder-bounded dimensions do. Settable *and* bounded is
    /// achieved by not caching it at all.
    /// </summary>
    [Fact]
    public void A_custom_background_makes_the_key_uncacheable_while_the_default_stays_cacheable()
    {
        var custom = ImageTransforms.Resolve(
            new TransformRequest(256, null, "jpeg", null, null, "ff0000"), "image/png", 800, 600);
        var defaulted = ImageTransforms.Resolve(
            new TransformRequest(256, null, "jpeg", null, null, null), "image/png", 800, 600);

        Assert.False(custom.IsCacheable);
        Assert.True(defaulted.IsCacheable);
        Assert.Null(defaulted.Background);
    }
}
