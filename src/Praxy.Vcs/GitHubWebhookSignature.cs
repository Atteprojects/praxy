using System.Security.Cryptography;
using System.Text;

namespace Praxy.Vcs;

/// <summary>
/// GitHub's own webhook signing scheme — <c>X-Hub-Signature-256: sha256=&lt;hex HMAC-SHA256(secret,
/// rawBody)&gt;</c>, the raw body only, no timestamp mixed in. Deliberately NOT
/// <c>Praxy.Webhooks.WebhookSignature</c>, which implements a different, Stripe-style scheme
/// (<c>v1=&lt;hex HMAC-SHA256(timestamp + "." + body)&gt;</c>) for Praxy's own outbound deliveries —
/// this is GitHub's wire format for inbound deliveries, a different scheme entirely. Reuses the same
/// constant-time-comparison discipline via <see cref="CryptographicOperations.FixedTimeEquals"/>.
/// </summary>
public static class GitHubWebhookSignature
{
    private const string Prefix = "sha256=";

    /// <summary><paramref name="rawBody"/> must be the exact bytes GitHub signed — read before any model binding touches the request body.</summary>
    public static bool Verify(string secret, ReadOnlySpan<byte> rawBody, string? headerValue)
    {
        if (string.IsNullOrEmpty(headerValue) || !headerValue.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody);
        var expected = Encoding.UTF8.GetBytes(Convert.ToHexStringLower(hash));
        var provided = Encoding.UTF8.GetBytes(headerValue[Prefix.Length..]);
        return expected.Length == provided.Length && CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
