using Praxy.Storage;

namespace Praxy.Tests.Unit;

public class MimeTypesTests
{
    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("IMAGE/PNG", "image/png")]
    [InlineData("text/plain; charset=utf-8", "text/plain")]
    [InlineData("  application/pdf  ", "application/pdf")]
    public void Normalize_strips_parameters_and_lower_cases(string header, string expected) =>
        Assert.Equal(expected, MimeTypes.Normalize(header));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a mime type")]
    [InlineData("image")]
    public void Normalize_falls_back_rather_than_rejecting_an_unusable_content_type(string? header) =>
        Assert.Equal(MimeTypes.Fallback, MimeTypes.Normalize(header));

    [Fact]
    public void A_null_or_empty_allow_list_means_any_type()
    {
        Assert.True(MimeTypes.IsAllowed(null, "application/zip"));
        Assert.True(MimeTypes.IsAllowed([], "application/zip"));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", false)]
    [InlineData("text/plain", true)]
    public void Exact_entries_match_only_themselves(string mimeType, bool allowed) =>
        Assert.Equal(allowed, MimeTypes.IsAllowed(["image/png", "text/plain"], mimeType));

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/svg+xml", true)]
    [InlineData("video/mp4", false)]
    // The wildcard keeps the slash, so a type whose name merely starts with "image" is not a match.
    [InlineData("imagex/png", false)]
    public void A_type_wildcard_matches_that_type_only(string mimeType, bool allowed) =>
        Assert.Equal(allowed, MimeTypes.IsAllowed(["image/*"], mimeType));

    [Fact]
    public void The_full_wildcard_matches_everything()
    {
        Assert.True(MimeTypes.IsAllowed(["*/*"], "application/octet-stream"));
        Assert.True(MimeTypes.IsAllowed(["*"], "video/mp4"));
    }

    [Fact]
    public void Matching_is_case_insensitive_on_both_sides() =>
        Assert.True(MimeTypes.IsAllowed(["IMAGE/PNG"], "image/png"));

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/*", true)]
    [InlineData("*/*", true)]
    [InlineData("*", true)]
    [InlineData("application/vnd.api+json", true)]
    [InlineData("image", false)]
    [InlineData("image/", false)]
    [InlineData("/png", false)]
    [InlineData("*/png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Allow_list_entries_are_validated_as_they_are_stored(string? pattern, bool valid) =>
        Assert.Equal(valid, MimeTypes.IsValidPattern(pattern));
}
