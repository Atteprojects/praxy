namespace Praxy.Persistence.Entities;

/// <summary>
/// One GitHub App installation on some account (user or org). Instance-wide, deliberately with no
/// <c>ProjectId</c> — Sites Phase 4's own explicit call: a single GitHub App installation can cover
/// repositories used by any project, any resource type, so it doesn't belong to one. The first table
/// in this schema without a project scope (see docs/handoff/sites-phase-4-report.md).
///
/// A row here is purely a cheap "has GitHub been connected to this instance at all" existence check —
/// which specific installation actually covers a given repository is always resolved live against
/// GitHub's own API (<c>Praxy.Vcs.GitHubAppService</c>), never by joining through this table, so it
/// never goes stale relative to what GitHub actually grants.
/// </summary>
public class VcsInstallation
{
    public required Guid Id { get; set; }
    public required long InstallationId { get; set; }
    public required string AccountLogin { get; set; }
    public required string AccountType { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
