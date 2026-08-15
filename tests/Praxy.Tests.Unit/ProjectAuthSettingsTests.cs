using System.Text.Json.Nodes;
using Praxy.Auth;

namespace Praxy.Tests.Unit;

public class ProjectAuthSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"other":"stuff"}""")]
    [InlineData("not json at all")]
    [InlineData("""{"auth": 42}""")]
    public void Missing_or_malformed_settings_yield_defaults(string json)
    {
        var settings = ProjectAuthSettings.Parse(json);
        Assert.True(settings.EmailPassword);
        Assert.False(settings.GoogleEnabled);
        Assert.Equal(10, settings.SessionLimit);
        Assert.Equal(8, settings.PasswordMinLength);
    }

    [Fact]
    public void ApplyTo_roundtrips_and_preserves_unrelated_keys()
    {
        var updated = new ProjectAuthSettings
        {
            EmailPassword = false,
            GoogleEnabled = true,
            GoogleClientId = "cid",
            GoogleClientSecret = "csecret",
            SessionLimit = 3,
            PasswordMinLength = 12,
        };
        var json = updated.ApplyTo("""{"unrelated":{"keep":true}}""");

        var parsed = ProjectAuthSettings.Parse(json);
        Assert.Equal(updated, parsed);
        Assert.True(JsonNode.Parse(json)!["unrelated"]!["keep"]!.GetValue<bool>());
    }

    [Fact]
    public void Out_of_range_values_clamp_instead_of_breaking_auth()
    {
        var settings = ProjectAuthSettings.Parse(
            """{"auth":{"sessionLimit":100000,"passwordMinLength":1}}""");
        Assert.Equal(ProjectAuthSettings.MaxSessionLimit, settings.SessionLimit);
        Assert.Equal(8, settings.PasswordMinLength);
    }
}
