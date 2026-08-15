using System.Security.Cryptography;
using System.Text;

namespace Praxy.Auth;

/// <summary>
/// Single-purpose secrets (verification, recovery, OAuth token flow, team invites):
/// 32 bytes of CSPRNG output as base64url on the wire, SHA-256 hex at rest.
/// </summary>
public static class Secrets
{
    public static (string Secret, string Hash) Generate()
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (secret, Hash(secret));
    }

    /// <summary>Hash of the wire form itself — callers never need to round-trip to raw bytes.</summary>
    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool HashEquals(string? storedHex, string candidateHex) =>
        storedHex is not null &&
        storedHex.Length == candidateHex.Length &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(storedHex), Convert.FromHexString(candidateHex));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.Length % 4 == 0 ? b64 : b64 + new string('=', 4 - b64.Length % 4));
    }
}
