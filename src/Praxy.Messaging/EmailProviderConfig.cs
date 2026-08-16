using System.Text.Json;
using System.Text.Json.Serialization;

namespace Praxy.Messaging;

/// <summary>
/// The non-secret half of an <c>email</c>-type <see cref="Praxy.Persistence.Entities.MessagingProvider"/>'s
/// <c>Config</c> jsonb column — everything <see cref="Praxy.Auth.SmtpOptions"/> needs except the
/// password, which lives separately as <see cref="Praxy.Persistence.Entities.MessagingProvider.ProtectedSecret"/>
/// (encrypted) so a provider list response can round-trip this shape without ever touching the secret.
/// </summary>
public sealed record EmailProviderConfig(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("useTls")] bool UseTls)
{
    public static EmailProviderConfig Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<EmailProviderConfig>(json);
            // System.Text.Json fills a missing non-nullable property with its type default (null,
            // for string) rather than throwing — coalesce so Host/From honor their non-null type.
            return parsed is null ? new EmailProviderConfig("", 587, null, "", true) : parsed with
            {
                Host = parsed.Host ?? "",
                From = parsed.From ?? "",
            };
        }
        catch (JsonException)
        {
            return new EmailProviderConfig("", 587, null, "", true);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
