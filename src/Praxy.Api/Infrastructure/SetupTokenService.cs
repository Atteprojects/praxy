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
    private string? _token;

    public bool Required =>
        !string.IsNullOrWhiteSpace(config["PRAXY_PUBLIC_URL"] ?? config["Praxy:PublicUrl"]);

    /// <summary>Called once at startup, after migrations, when the instance is still unclaimed.</summary>
    public void GenerateAndAnnounce()
    {
        if (!Required)
            return;
        _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        logger.LogWarning(
            "Instance is unclaimed and PRAXY_PUBLIC_URL is set. Claiming requires this setup token: {SetupToken}",
            _token);
    }

    public bool Validate(string? candidate)
    {
        if (!Required)
            return true;
        if (_token is null || string.IsNullOrEmpty(candidate))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(_token));
    }
}
