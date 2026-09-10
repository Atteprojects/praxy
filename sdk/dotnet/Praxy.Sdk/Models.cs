using System.Text.Json.Serialization;

namespace Praxy.Sdk;

/// <summary>
/// The hand-written models — the ones the generator is told to reuse rather than emit.
///
/// <para>Every property that the API may omit is nullable, and that is not the same as "may be
/// null". <c>Program.cs</c> serializes with <c>WhenWritingNull</c>, so an unset property is
/// <em>absent from the JSON object</em> rather than present-and-null. System.Text.Json leaves an
/// absent property at its default, which is exactly the behaviour wanted here — but it is the
/// reason a required property must not be modelled as nullable just to be safe: that would turn a
/// contract violation into a silent null far from where it happened.</para>
/// </summary>
public sealed record AppUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("emailVerified")] bool EmailVerified,
    [property: JsonPropertyName("status")] bool Status,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("prefs")] IReadOnlyDictionary<string, object?> Prefs,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

/// <summary>One of an app user's sessions.</summary>
public sealed record AppSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("ip")] string? Ip,
    [property: JsonPropertyName("userAgent")] string? UserAgent,
    [property: JsonPropertyName("current")] bool Current,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <summary>A page of sessions.</summary>
public sealed record SessionList(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("sessions")] IReadOnlyList<AppSession> Sessions);
