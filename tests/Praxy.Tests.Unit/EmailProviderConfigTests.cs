using Praxy.Messaging;

namespace Praxy.Tests.Unit;

public class EmailProviderConfigTests
{
    [Fact]
    public void Roundtrips_through_json()
    {
        var config = new EmailProviderConfig("smtp.example.com", 587, "user", "noreply@example.com", true);
        var parsed = EmailProviderConfig.Parse(config.ToJson());
        Assert.Equal(config, parsed);
    }

    [Fact]
    public void Null_username_roundtrips()
    {
        var config = new EmailProviderConfig("smtp.example.com", 25, null, "noreply@example.com", false);
        Assert.Null(EmailProviderConfig.Parse(config.ToJson()).Username);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public void Unreadable_json_yields_the_full_safe_default_instead_of_throwing(string json)
    {
        var config = EmailProviderConfig.Parse(json);
        Assert.Equal("", config.Host);
        Assert.Equal(587, config.Port);
    }

    [Fact]
    public void A_missing_host_never_surfaces_as_null_despite_Systems_Text_Json_defaulting_it_so()
    {
        // "{}" deserializes successfully (no exception) with Host/From left at System.Text.Json's
        // type default — null for a non-nullable string — which Parse must still coalesce away.
        var config = EmailProviderConfig.Parse("{}");
        Assert.Equal("", config.Host);
        Assert.Equal("", config.From);
    }
}
