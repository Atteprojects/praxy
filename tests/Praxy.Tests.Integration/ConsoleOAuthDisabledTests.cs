using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>Default settings configure no instance-level Google client — the feature stays off.</summary>
public class ConsoleOAuthDisabledTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    [Fact]
    public async Task Capabilities_reports_google_oauth_disabled_and_the_start_endpoint_refuses_it()
    {
        var caps = await ReadJson(await Client.GetAsync("/v1/console/capabilities"));
        Assert.False(caps.GetProperty("googleOAuthEnabled").GetBoolean());

        var start = await Client.GetAsync("/v1/console/sessions/oauth2/google");
        await AssertError(start, 400, ErrorTypes.ConsoleOAuthNotConfigured);
    }
}
