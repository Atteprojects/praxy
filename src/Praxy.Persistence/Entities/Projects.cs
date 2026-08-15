namespace Praxy.Persistence.Entities;

public class Project
{
    /// <summary>Wire-visible id: custom (validated) or generated. <c>console</c> is reserved.</summary>
    public required string Id { get; set; }

    /// <summary>Null only for the reserved console project, which belongs to no organization.</summary>
    public Guid? OrganizationId { get; set; }

    public required string Name { get; set; }
    public string Settings { get; set; } = "{}";

    /// <summary>Stamped by the first data-plane ping; drives the console's onboarding checklist.</summary>
    public DateTimeOffset? LastPingAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>CORS / origin allowlist entry. Enforced from Phase 1.</summary>
public class Platform
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }

    /// <summary>web | flutter-android | flutter-ios | ...</summary>
    public required string Type { get; set; }

    public required string Name { get; set; }
    public string? Hostname { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ApiKey
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Name { get; set; }
    public required string SecretHash { get; set; }
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
