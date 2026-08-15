namespace Praxy.Persistence.Entities;

/// <summary>
/// Project-scoped account. Console operators are the users of the reserved <c>console</c> project;
/// two projects may legitimately hold the same email.
/// </summary>
public class User
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Email { get; set; }
    public string? PasswordHash { get; set; }
    public string Name { get; set; } = "";
    public bool EmailVerified { get; set; }
    public bool Status { get; set; } = true;
    public string[] Labels { get; set; } = [];
    public string Prefs { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Session
{
    public required Guid Id { get; set; }
    public required Guid UserId { get; set; }

    /// <summary>SHA-256 of the 32-byte CSPRNG secret. The secret itself is never stored.</summary>
    public required string SecretHash { get; set; }

    /// <summary>email | google | token ...</summary>
    public required string Provider { get; set; }

    public string? Ip { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>Present from day one so TOTP MFA (Phase 2+) is not a migration.</summary>
    public bool MfaVerified { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
