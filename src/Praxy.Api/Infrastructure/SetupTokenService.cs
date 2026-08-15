using System.Security.Cryptography;
using System.Text;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// When <c>PRAXY_PUBLIC_URL</c> is set, the instance is assumed reachable from the internet and
/// claiming requires the one-time setup token printed to the container logs. Regenerated on every
/// restart while unclaimed; never persisted.
/// </summary>
public sealed class SetupTokenService(IConfiguration config, ILogger<SetupTokenService> logger)
{
    /// <summary>The active token, null unless generated. Never serialized into any response.</summary>
    public string? Token { get; private set; }

    public bool Required =>
        !string.IsNullOrWhiteSpace(config["PRAXY_PUBLIC_URL"] ?? config["Praxy:PublicUrl"]);

    /// <summary>Called once at startup, after migrations, when the instance is still unclaimed.</summary>
    public void GenerateAndAnnounce()
    {
        if (!Required)
            return;
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        logger.LogWarning(
            "Instance is unclaimed and PRAXY_PUBLIC_URL is set. Claiming requires this setup token: {SetupToken}",
            Token);
    }

    public bool Validate(string? candidate)
    {
        if (!Required)
            return true;
        if (Token is null || string.IsNullOrEmpty(candidate))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(Token));
    }
}
