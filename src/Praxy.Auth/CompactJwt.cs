using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Praxy.Auth;

/// <summary>
/// Minimal HS256 JWT — mint and verify only, no external claims processing. Used for values
/// this instance both issues and consumes within seconds (the OAuth callback secret wrap and
/// the OAuth state cookie), so a full JWT library buys nothing.
/// </summary>
public static class CompactJwt
{
    private static readonly string Header =
        Secrets.Base64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));

    public static string Encode(byte[] key, JsonObject claims, TimeSpan lifetime)
    {
        claims["exp"] = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var payload = Secrets.Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signingInput = $"{Header}.{payload}";
        var signature = Secrets.Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
    }

    /// <summary>Null when the signature is invalid, the shape is wrong, or <c>exp</c> has passed.</summary>
    public static JsonObject? Decode(byte[] key, string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;

        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"));
        byte[] actual;
        try
        {
            actual = Secrets.FromBase64Url(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            return null;

        try
        {
            var claims = JsonNode.Parse(Secrets.FromBase64Url(parts[1])) as JsonObject;
            if (claims?["exp"] is not JsonValue expValue || !expValue.TryGetValue<long>(out var exp))
                return null;
            return DateTimeOffset.FromUnixTimeSeconds(exp) <= DateTimeOffset.UtcNow ? null : claims;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
