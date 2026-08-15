using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Persistence.Entities;

namespace Praxy.Persistence;

/// <summary>
/// The system catalog — schema <c>praxy</c>, owned exclusively by EF Core migrations.
/// User-defined tables (Phase 2+) live in <c>px_*</c> schemas and are raw Npgsql; EF never touches them.
/// </summary>
public class PraxyDb(DbContextOptions<PraxyDb> options) : DbContext(options)
{
    public const string Schema = "praxy";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Platform> Platforms => Set<Platform>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Token> Tokens => Set<Token>();
    public DbSet<Identity> Identities => Set<Identity>();
    public DbSet<SchemaJob> SchemaJobs => Set<SchemaJob>();
    public DbSet<OutboxEvent> Events => Set<OutboxEvent>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Organization>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Limits).HasColumnType("jsonb");
        });

        b.Entity<OrganizationMember>(e =>
        {
            e.HasKey(x => new { x.OrganizationId, x.UserId });
            e.Property(x => x.Role).HasMaxLength(32);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Project>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Settings).HasColumnType("jsonb");
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.OrganizationId);
        });

        b.Entity<Platform>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Hostname).HasMaxLength(256);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProjectId);
        });

        b.Entity<ApiKey>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProjectId);
            // Defense in depth: the reserved console project can never hold API keys,
            // even if application-level guards slip.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_api_keys_project_not_console",
                $"project_id <> '{Ids.ConsoleProjectId}'"));
        });

        b.Entity<User>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Prefs).HasColumnType("jsonb");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Email }).IsUnique();
        });

        b.Entity<Session>(e =>
        {
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Ip).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Team>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Prefs).HasColumnType("jsonb");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProjectId);
        });

        b.Entity<Membership>(e =>
        {
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TeamId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Token>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Type });
        });

        b.Entity<Identity>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ProviderUid).HasMaxLength(256);
            e.Property(x => x.ProviderEmail).HasMaxLength(320);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Provider, x.ProviderUid }).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<SchemaJob>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => new { x.DatabaseId, x.Status });
        });

        b.Entity<OutboxEvent>(e =>
        {
            e.ToTable("events");
            e.Property(x => x.Type).HasMaxLength(256);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Actor).HasMaxLength(128);
            e.Property(x => x.Action).HasMaxLength(128);
            e.Property(x => x.Resource).HasMaxLength(256);
            e.Property(x => x.Ip).HasMaxLength(64);
            e.HasIndex(x => x.ProjectId);
        });
    }
}
