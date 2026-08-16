using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;

namespace Praxy.Api.Endpoints;

public static class CapabilitiesEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        // Server-driven feature flags the console gates screens on. Unauthenticated: the
        // login/claim screen itself depends on `claimed`. Features flip on phase by phase.
        api.MapGet("/v1/console/capabilities", async (ConsoleAuthService auth, SetupTokenService setupTokens, CancellationToken ct) =>
            Results.Ok(new
            {
                version = PraxyVersion.Current,
                claimed = await auth.IsClaimedAsync(ct),
                setupTokenRequired = setupTokens.Required,
                features = new
                {
                    auth = true,
                    databases = true,
                    realtime = false,
                    messaging = false,
                    functions = false,
                    webhooks = false,
                },
            }));
    }
}
