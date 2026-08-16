using System.Security.Cryptography;
using System.Text;

namespace Praxy.Webhooks;

/// <summary>
/// The wire header names a delivery carries, per docs/research/appwrite-api.md's "adopt the shape,
/// fix the crypto": Appwrite's <c>X-Appwrite-Webhook-*</c> family, minus <c>User-Id</c> (Praxy's
/// events aren't always user-scoped) and with the signature split into its own timestamp header.
/// </summary>
public static class WebhookHeaders
{
    public const string Id = "X-Praxy-Webhook-Id";
    public const string Events = "X-Praxy-Webhook-Events";
    public const string Name = "X-Praxy-Webhook-Name";
    public const string ProjectId = "X-Praxy-Webhook-ProjectId";
    public const string Signature = "X-Praxy-Webhook-Signature";
    public const string Timestamp = "X-Praxy-Webhook-Timestamp";
}

/// <summary>
/// Stripe's signing scheme, chosen over Appwrite's <c>base64(HMAC-SHA1(url + body))</c> specifically
/// because it's replay-resistant and needs no URL-canonicalization games (research/appwrite-api.md):
/// <c>v1=&lt;hex HMAC-SHA256(timestamp + "." + body)&gt;</c> with the timestamp carried in a
/// separate header so a verifier can reject stale deliveries before even touching the signature.
/// </summary>
public static class WebhookSignature
{
    public static string Sign(string secret, long timestampUnixSeconds, string body)
    {
        var signed = Encoding.UTF8.GetBytes($"{timestampUnixSeconds}.{body}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        return $"v1={Convert.ToHexStringLower(hash)}";
    }

    public static bool Verify(string secret, long timestampUnixSeconds, string body, string signatureHeader)
    {
        var expected = Sign(secret, timestampUnixSeconds, body);
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(signatureHeader);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
