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

    /// <summary>
    /// Off by default (architecture.md §5: "that bypass is exactly the flag that leaks data when
    /// it defaults wrong"). On: this key skips the data plane's permission layer entirely, the same
    /// way a trusted server integration works — row CRUD skips table- and row-level filtering, and
    /// function invocation skips the per-function <c>execute</c> role check. Scopes still apply in
    /// both cases; this bypasses permissions, never authentication or scoping.
    ///
    /// The name predates functions having a permission model at all. It is kept because renaming it
    /// is a breaking wire change to a field SDKs may read; <c>bypassPermissions</c> is the v1.1
    /// candidate.
    /// </summary>
    public bool BypassRowPermissions { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
