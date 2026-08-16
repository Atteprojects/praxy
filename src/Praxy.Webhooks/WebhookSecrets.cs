using System.Security.Cryptography;

namespace Praxy.Webhooks;

/// <summary>
/// Webhook signing secrets: 32 bytes of CSPRNG output as base64url, kept in plaintext (see
/// <c>WebhookSubscription.Secret</c>'s doc comment — HMAC signing needs the raw value on every
/// delivery, so this is deliberately not the hash-at-rest shape <c>Praxy.Auth.Secrets</c> uses).
/// </summary>
public static class WebhookSecrets
{
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
