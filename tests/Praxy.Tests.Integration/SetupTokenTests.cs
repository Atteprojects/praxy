using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Praxy.Api.Infrastructure;
using Praxy.Core.Errors;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>When PRAXY_PUBLIC_URL is set, claiming needs the setup token printed to the logs.</summary>
public class SetupTokenTests(PostgresContainerFixture pg) : ApiTestBase(pg)
{
    protected override IDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>
    {
        ["PRAXY_PUBLIC_URL"] = "https://praxy.example.com",
    };

    [Fact]
    public async Task Claim_requires_the_setup_token_when_public_url_is_set()
    {
        var caps = await ReadJson(await Client.GetAsync("/v1/console/capabilities"));
        Assert.True(caps.GetProperty("setupTokenRequired").GetBoolean());

        var withoutToken = await Client.PostAsJsonAsync("/v1/console/claim",
            new { email = "owner@praxy.test", password = "hunter2hunter2" });
        await AssertError(withoutToken, 401, ErrorTypes.InstanceSetupTokenInvalid);

        var wrongToken = await Client.PostAsJsonAsync("/v1/console/claim",
            new { email = "owner@praxy.test", password = "hunter2hunter2", setupToken = "0000000000000000" });
        await AssertError(wrongToken, 401, ErrorTypes.InstanceSetupTokenInvalid);

        // The real token — in production read from the container logs; here from the service.
        var token = Factory.Services.GetRequiredService<SetupTokenService>().Token;
        Assert.False(string.IsNullOrEmpty(token));
        await ClaimAsync(setupToken: token);
    }
}
