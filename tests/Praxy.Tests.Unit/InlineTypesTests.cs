using Praxy.Storage;

namespace Praxy.Tests.Unit;

/// <summary>
/// Inline serving is two gates — the bucket opts a type in, and the hard-coded set agrees — and the
/// hard-coded set is the security control, since a file's stored MIME type is whatever the uploader
/// sent. See <c>StorageTransfer.DownloadAsync</c>'s remarks for the attack this closes.
/// </summary>
public class InlineTypesTests
{
    /// <summary>Permanently excluded, not configurable: HTML is the attack, and SVG carries script.</summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/xhtml+xml")]
    [InlineData("text/xml")]
    [InlineData("application/javascript")]
    [InlineData("application/octet-stream")]
    public void An_unsafe_type_is_never_inline_even_when_the_bucket_asks_for_it(string mimeType)
    {
        Assert.False(InlineTypes.IsSafe(mimeType));
        // The bucket asking is not enough — this is the intersection that makes it safe.
        Assert.False(InlineTypes.ServesInline([mimeType], mimeType));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("video/mp4")]
    [InlineData("application/pdf")]
    [InlineData("text/plain")]
    public void A_safe_type_serves_inline_only_once_the_bucket_opts_it_in(string mimeType)
    {
        Assert.True(InlineTypes.IsSafe(mimeType));
        // Default state: empty list, so nothing is inline.
        Assert.False(InlineTypes.ServesInline(null, mimeType));
        Assert.False(InlineTypes.ServesInline([], mimeType));
        // A list that opts in some *other* type doesn't opt in this one.
        Assert.False(InlineTypes.ServesInline(["image/gif"], "video/mp4"));
        Assert.True(InlineTypes.ServesInline([mimeType], mimeType));
    }

    [Fact]
    public void Matching_is_exact_and_case_insensitive_with_no_wildcards()
    {
        Assert.True(InlineTypes.ServesInline(["image/png"], "IMAGE/PNG"));
        // No wildcard support: "image/*" is not a type and can never opt anything in.
        Assert.False(InlineTypes.ServesInline(["image/*"], "image/png"));
        Assert.False(InlineTypes.ServesInline(["image/png"], "image/png2"));
    }

    [Fact]
    public void The_safe_set_contains_nothing_a_browser_parses_as_a_document()
    {
        Assert.DoesNotContain("text/html", InlineTypes.Safe);
        Assert.DoesNotContain("image/svg+xml", InlineTypes.Safe);
        Assert.All(InlineTypes.Safe, type => Assert.DoesNotContain("html", type));
        Assert.All(InlineTypes.Safe, type => Assert.DoesNotContain("xml", type));
    }
}
