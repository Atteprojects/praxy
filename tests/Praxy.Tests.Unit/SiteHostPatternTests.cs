using Praxy.Sites;

namespace Praxy.Tests.Unit;

public class SiteHostPatternTests
{
    private const string Domain = "sites.localhost";

    [Fact]
    public void Valid_2_label_production_hostname_parses_with_no_deployment_ref()
    {
        Assert.True(SiteHostPattern.TryParse(
            "blog.myproj.sites.localhost", Domain, out var key, out var projectId, out var deploymentRef));
        Assert.Equal("blog", key);
        Assert.Equal("myproj", projectId);
        Assert.Null(deploymentRef);
    }

    [Fact]
    public void Valid_3_label_preview_hostname_parses_all_three_labels()
    {
        Assert.True(SiteHostPattern.TryParse(
            "a1b2c3.blog.myproj.sites.localhost", Domain, out var key, out var projectId, out var deploymentRef));
        Assert.Equal("blog", key);
        Assert.Equal("myproj", projectId);
        Assert.Equal("a1b2c3", deploymentRef);
    }

    [Fact]
    public void The_2_arg_overload_still_works_and_ignores_a_3rd_label()
    {
        Assert.True(SiteHostPattern.TryParse("a1b2c3.blog.myproj.sites.localhost", Domain, out var key, out var projectId));
        Assert.Equal("blog", key);
        Assert.Equal("myproj", projectId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sites.localhost")] // the bare domain, no labels at all
    [InlineData("myproj.sites.localhost")] // 1 label — not a valid site or preview shape
    [InlineData(".myproj.sites.localhost")] // empty leading label
    [InlineData("blog..sites.localhost")] // empty middle label
    [InlineData("blog.myproj.sites.localhost.evil.com")] // suffix match only, not exact
    [InlineData("blog.myproj.notsites.localhost")] // wrong domain entirely
    public void Malformed_2_label_variants_are_rejected(string host) =>
        Assert.False(SiteHostPattern.TryParse(host, Domain, out _, out _, out _));

    [Theory]
    [InlineData("..blog.myproj.sites.localhost")] // empty deployment-ref label
    [InlineData("a1b2c3..myproj.sites.localhost")] // empty key label
    [InlineData("a1b2c3.blog..sites.localhost")] // empty project label
    [InlineData("a1.b2.c3.blog.myproj.sites.localhost")] // 4 labels — too deep for either shape
    public void Malformed_3_label_variants_are_rejected(string host) =>
        Assert.False(SiteHostPattern.TryParse(host, Domain, out _, out _, out _));

    [Fact]
    public void Host_or_domain_matching_is_case_insensitive()
    {
        Assert.True(SiteHostPattern.TryParse(
            "BLOG.MYPROJ.SITES.LOCALHOST", Domain, out var key, out var projectId, out var deploymentRef));
        Assert.Equal("BLOG", key);
        Assert.Equal("MYPROJ", projectId);
        Assert.Null(deploymentRef);
    }
}
