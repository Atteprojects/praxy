namespace Praxy.Persistence.Entities;

/// <summary>
/// Short-lived, single-purpose secrets (email verification, password recovery, OAuth token flow).
/// Only the SHA-256 of the secret is stored; consumption deletes the row.
/// </summary>
public class Token
{
    public required Guid Id { get; set; }
    public required Guid UserId { get; set; }

    /// <summary>verification | recovery | oauth2</summary>
    public required string Type { get; set; }

    public required string SecretHash { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>An OAuth provider identity linked to an app user. Provider uid is unique per project + provider.</summary>
public class Identity
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required Guid UserId { get; set; }

    /// <summary>github (Google etc. slot in behind the same provider abstraction later).</summary>
    public required string Provider { get; set; }

    public required string ProviderUid { get; set; }
    public string? ProviderEmail { get; set; }

    /// <summary>Provider access token, AES-GCM encrypted with the instance key — never plaintext at rest.</summary>
    public string? AccessTokenEnc { get; set; }

    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
