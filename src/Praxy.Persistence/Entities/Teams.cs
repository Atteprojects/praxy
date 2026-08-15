namespace Praxy.Persistence.Entities;

/// <summary>Project-scoped team of app users. Membership roles feed <c>team:&lt;id&gt;/&lt;role&gt;</c> permission roles.</summary>
public class Team
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Name { get; set; }
    public string Prefs { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Membership
{
    public required Guid Id { get; set; }
    public required Guid TeamId { get; set; }
    public required Guid UserId { get; set; }

    /// <summary>Free-form role names within the team (e.g. <c>owner</c>). Feed <c>team:&lt;id&gt;/&lt;role&gt;</c>.</summary>
    public string[] Roles { get; set; } = [];

    /// <summary>False while an emailed invitation is pending; direct (server/console) adds start true.</summary>
    public bool Confirmed { get; set; }

    /// <summary>SHA-256 of the invitation secret. Null for direct adds; cleared on acceptance.</summary>
    public string? SecretHash { get; set; }

    public DateTimeOffset? InvitedAt { get; set; }
    public DateTimeOffset? JoinedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
