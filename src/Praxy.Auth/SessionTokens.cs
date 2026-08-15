using System.Security.Cryptography;

namespace Praxy.Auth;

/// <summary>
/// Opaque session tokens: <c>&lt;sessionId&gt;.&lt;secret&gt;</c> where the secret is 32 bytes of
/// CSPRNG output (base64url). Only SHA-256(secret) is stored; lookup is by session id and the
/// hash comparison is constant-time.
/// </summary>
public static class SessionTokens
{
    public const int SecretBytes = 32;

    public static (string Token, string SecretHash) Create(Guid sessionId)
    {
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var token = $"{sessionId:n}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        return (token, HashSecret(secret));
    }

    public static bool TryParse(string token, out Guid sessionId, out string secretHash)
    {
        sessionId = default;
        secretHash = "";
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
            return false;
        if (!Guid.TryParseExact(token[..dot], "n", out sessionId))
            return false;

        var b64 = token[(dot + 1)..].Replace('-', '+').Replace('_', '/');
        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(b64.Length % 4 == 0 ? b64 : b64 + new string('=', 4 - b64.Length % 4));
        }
        catch (FormatException)
        {
            return false;
        }

        secretHash = HashSecret(secret);
        return true;
    }

    public static bool HashEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b));

    private static string HashSecret(byte[] secret) => Convert.ToHexStringLower(SHA256.HashData(secret));
}
