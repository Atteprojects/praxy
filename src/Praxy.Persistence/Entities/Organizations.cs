namespace Praxy.Persistence.Entities;

/// <summary>
/// Modeled fully from Phase 0. Surfaced in the console as the owner of the projects list (name +
/// id in the URL); creating, renaming and multi-org switching still do not exist.
/// </summary>
public class Organization
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Org-level quotas, enforced in Phase 9. Raw jsonb.</summary>
    public string Limits { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class OrganizationMember
{
    public required Guid OrganizationId { get; set; }
    public required Guid UserId { get; set; }

    /// <summary>owner | member</summary>
    public required string Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
