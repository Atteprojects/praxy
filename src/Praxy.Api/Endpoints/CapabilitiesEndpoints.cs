using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;

namespace Praxy.Api.Endpoints;

/// <summary>Which features this build serves. The console gates whole screens on these.</summary>
public sealed record CapabilityFeatures(
    bool Auth, bool Databases, bool Realtime, bool Messaging, bool Functions, bool Webhooks);

public sealed record CapabilitiesResponse(
    string Version, bool Claimed, bool SetupTokenRequired, CapabilityFeatures Features);

public static class CapabilitiesEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        // Server-driven feature flags the console gates screens on. Unauthenticated: the
        // login/claim screen itself depends on `claimed`. Features flip on phase by phase.
        api.MapGet("/v1/console/capabilities", async (ConsoleAuthService auth, SetupTokenService setupTokens, CancellationToken ct) =>
            Results.Ok(new CapabilitiesResponse(
                PraxyVersion.Current,
                await auth.IsClaimedAsync(ct),
                setupTokens.Required,
                new CapabilityFeatures(
                    Auth: true,
                    Databases: true,
                    Realtime: true,
                    Messaging: true,
                    Functions: true,
                    Webhooks: true))))
            .Produces<CapabilitiesResponse>();
    }
}
