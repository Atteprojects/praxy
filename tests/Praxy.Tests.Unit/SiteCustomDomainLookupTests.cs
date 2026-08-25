using Praxy.Sites;

namespace Praxy.Tests.Unit;

/// <summary>
/// <see cref="SiteCustomDomainLookup.Normalize"/> is the one pure piece of the custom-domain lookup —
/// everything else (<c>FindAsync</c>/<c>ResolveEnabledSiteAsync</c>) is a database round trip, exercised
/// by <c>SitesAskTlsTests</c> and <c>SiteCustomDomainTests</c> instead. Mirrors
/// <see cref="SiteHostPatternTests"/>'s case-insensitivity coverage for the built-in subdomain path,
/// since a registered hostname and an incoming request's <c>Host</c>/<c>?domain=</c> value must compare
/// equal regardless of casing either side happens to use.
/// </summary>
public class SiteCustomDomainLookupTests
{
    [Theory]
    [InlineData("Example.Com", "example.com")]
    [InlineData("EXAMPLE.COM", "example.com")]
    [InlineData("  example.com  ", "example.com")]
    [InlineData("already-lower.example.com", "already-lower.example.com")]
    public void Normalize_lowercases_and_trims(string input, string expected) =>
        Assert.Equal(expected, SiteCustomDomainLookup.Normalize(input));
}
