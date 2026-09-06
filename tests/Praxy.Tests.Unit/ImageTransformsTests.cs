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

    /// <summary>
    /// security-review-phase-2 finding: the derived axis was never bounded by
    /// <see cref="DimensionLadder.TopRung"/>, only the caller-requested one — so a real, honestly
    /// encoded 4x100,000 screenshot (400,000 total source pixels, comfortably inside
    /// <c>MaxSourceImagePixels</c>) requesting <c>?width=64</c> derived a height of 1,600,000.
    /// Confirmed live against a real deployed instance: SkiaSharp's <c>Resize</c> allocated that
    /// target bitmap without complaint, and only libpng's own encoder limit caught it — by returning
    /// null from <c>SKBitmap.Encode</c>, which <see cref="ImageTransformer"/> didn't check, crashing
    /// with a <see cref="NullReferenceException"/> (500) instead of the clean 400 every other
    /// rejected transform gets. This must be a clean rejection at the same layer as every other
    /// dimension check, not a crash several layers downstream in the SkiaSharp encoder.
    /// </summary>
    [Fact]
    public void An_extreme_aspect_ratio_that_would_blow_up_the_derived_axis_is_rejected()
    {
        var ex = Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(64, null, null, null), Png, sourceWidth: 4, sourceHeight: 100_000));
        Assert.Equal(ErrorTypes.FileTransformInvalid, ex.Type);

        var ex2 = Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(null, 64, null, null), Png, sourceWidth: 100_000, sourceHeight: 4));
        Assert.Equal(ErrorTypes.FileTransformInvalid, ex2.Type);
    }

    /// <summary>
    /// The regression guard for that bound's *shape*. Bounding the derived axis at
    /// <see cref="DimensionLadder.TopRung"/> looks symmetric with the requested axis and passes a
    /// 1:1 boundary test, but only a perfectly square source can have both axes land under 2048 —
    /// so it rejects every ordinary photo at the top rung. These are all reasonable derivatives
    /// (2048x2731 is ~22 MB) and must not 400; the bound is on total pixels, which is what the
    /// allocation actually costs, not on either axis alone.
    /// </summary>
    [Theory]
    [InlineData(3000, 4000, 2048, 2731)]  // 3:4 phone photo
    [InlineData(1080, 1920, 2048, 3641)]  // 9:16 phone photo
    [InlineData(2000, 3000, 2048, 3072)]  // 2:3 camera portrait
    [InlineData(2480, 3508, 2048, 2897)]  // A4 document scan
    [InlineData(2048, 2048, 2048, 2048)]  // square — the case a per-axis bound would have allowed
    public void An_ordinary_photo_ratio_still_transforms_at_the_top_rung(
        int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight)
    {
        var key = ImageTransforms.Resolve(
            new TransformRequest(2048, null, null, null), Png, sourceWidth, sourceHeight);
        Assert.Equal(expectedWidth, key.Width);
        Assert.Equal(expectedHeight, key.Height);
    }

    /// <summary>The area bound itself: exactly at the limit passes, one rung's worth over does not.</summary>
    [Fact]
    public void The_output_pixel_bound_is_the_boundary_not_the_axis()
    {
        // 1:2 source at the top rung derives exactly 2048x4096 — MaxOutputPixels on the nose.
        var key = ImageTransforms.Resolve(new TransformRequest(2048, null, null, null), Png, 1000, 2000);
        Assert.Equal(2048, key.Width);
        Assert.Equal(4096, key.Height);
        Assert.Equal(DimensionLadder.MaxOutputPixels, key.Width * key.Height);

        // 1:3 at the same rung is over it — but still works at a lower rung, since the bound is on
        // the product rather than either axis.
        Assert.Throws<PraxyException>(() =>
            ImageTransforms.Resolve(new TransformRequest(2048, null, null, null), Png, 1000, 3000));
        var smaller = ImageTransforms.Resolve(new TransformRequest(1024, null, null, null), Png, 1000, 3000);
        Assert.Equal(1024, smaller.Width);
        Assert.Equal(3072, smaller.Height);
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
