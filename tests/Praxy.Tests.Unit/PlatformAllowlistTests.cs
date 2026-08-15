using Praxy.Auth;

namespace Praxy.Tests.Unit;

public class PlatformAllowlistTests
{
    private static readonly string[] Allowed = ["app.example.com", "*.preview.example.com", "localhost"];

    [Theory]
    [InlineData("https://app.example.com/callback")]
    [InlineData("https://APP.EXAMPLE.COM/callback")]
    [InlineData("http://localhost:5173/auth")]
    [InlineData("https://pr-42.preview.example.com/done?x=1")]
    [InlineData("https://a.b.preview.example.com/")]
    public void Allowlisted_redirects_pass(string url) =>
        Assert.True(PlatformAllowlist.RedirectAllowed(Allowed, url));

    [Theory]
    [InlineData("https://evil.com/phish")]
    [InlineData("https://app.example.com.evil.com/")]  // suffix trick
    [InlineData("https://preview.example.com/")]        // wildcard never matches the bare domain
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://app.example.com/")]
    [InlineData("app.example.com/relative")]
    [InlineData("")]
    public void Everything_else_is_refused(string url) =>
        Assert.False(PlatformAllowlist.RedirectAllowed(Allowed, url));

    [Theory]
    [InlineData("https://app.example.com", true)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("https://x.preview.example.com", true)]
    [InlineData("https://evil.com", false)]
    [InlineData("null", false)] // sandboxed-iframe Origin literal
    public void Origin_check_follows_the_same_rules(string origin, bool expected) =>
        Assert.Equal(expected, PlatformAllowlist.OriginAllowed(Allowed, origin));

    [Fact]
    public void Empty_allowlist_refuses_everything()
    {
        Assert.False(PlatformAllowlist.RedirectAllowed([], "https://anything.com/"));
        Assert.False(PlatformAllowlist.OriginAllowed([], "https://anything.com"));
    }
}
