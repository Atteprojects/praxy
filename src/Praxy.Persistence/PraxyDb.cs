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
    public DbSet<Database> Databases => Set<Database>();
    public DbSet<TableDef> Tables => Set<TableDef>();
    public DbSet<ColumnDef> Columns => Set<ColumnDef>();
    public DbSet<IndexDef> Indexes => Set<IndexDef>();
    public DbSet<TablePermission> TablePermissions => Set<TablePermission>();
    public DbSet<OutboxEvent> Events => Set<OutboxEvent>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<WebhookDeliveryAttempt> WebhookDeliveryAttempts => Set<WebhookDeliveryAttempt>();
    public DbSet<FunctionDef> Functions => Set<FunctionDef>();
    public DbSet<FunctionEnvVar> FunctionEnvVars => Set<FunctionEnvVar>();
    public DbSet<FunctionDeployment> FunctionDeployments => Set<FunctionDeployment>();
    public DbSet<FunctionDeploymentSource> FunctionDeploymentSources => Set<FunctionDeploymentSource>();
    public DbSet<FunctionExecution> FunctionExecutions => Set<FunctionExecution>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<SiteEnvVar> SiteEnvVars => Set<SiteEnvVar>();
    public DbSet<SiteDeployment> SiteDeployments => Set<SiteDeployment>();
    public DbSet<SiteDeploymentSource> SiteDeploymentSources => Set<SiteDeploymentSource>();
    public DbSet<SiteDeploymentScreenshot> SiteDeploymentScreenshots => Set<SiteDeploymentScreenshot>();
    public DbSet<MessagingProvider> MessagingProviders => Set<MessagingProvider>();
    public DbSet<MessagingTopic> MessagingTopics => Set<MessagingTopic>();
    public DbSet<MessagingTarget> MessagingTargets => Set<MessagingTarget>();
    public DbSet<MessagingSubscriber> MessagingSubscribers => Set<MessagingSubscriber>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageTarget> MessageTargets => Set<MessageTarget>();
    public DbSet<MessagingTemplate> MessagingTemplates => Set<MessagingTemplate>();

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
            e.HasIndex(x => new { x.TableId, x.Status });
        });

        b.Entity<Database>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.SchemaName).HasMaxLength(63);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Key }).IsUnique();
        });

        b.Entity<TableDef>(e =>
        {
            e.ToTable("tables");
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.PhysicalName).HasMaxLength(63);
            e.HasOne<Database>().WithMany().HasForeignKey(x => x.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.DatabaseId, x.Key }).IsUnique();
        });

        b.Entity<ColumnDef>(e =>
        {
            e.ToTable("columns");
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.PhysicalName).HasMaxLength(63);
            e.Property(x => x.DefaultValue).HasMaxLength(4096);
            e.Property(x => x.Format).HasMaxLength(32);
            e.Property(x => x.Options).HasColumnType("jsonb");
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasOne<TableDef>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TableId, x.Key }).IsUnique();
        });

        b.Entity<IndexDef>(e =>
        {
            e.ToTable("indexes");
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Type).HasMaxLength(16);
            e.Property(x => x.PhysicalName).HasMaxLength(63);
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasOne<TableDef>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TableId, x.Key }).IsUnique();
        });

        b.Entity<TablePermission>(e =>
        {
            e.ToTable("table_permissions");
            e.HasKey(x => new { x.TableId, x.Action, x.Role });
            e.Property(x => x.Action).HasMaxLength(16);
            e.Property(x => x.Role).HasMaxLength(128);
            e.HasOne<TableDef>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OutboxEvent>(e =>
        {
            e.ToTable("events");
            e.Property(x => x.Type).HasMaxLength(256);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt);
            // Each consumer claims independently — see the remarks on WebhooksDispatchedAt.
            e.HasIndex(x => x.WebhooksDispatchedAt);
            e.HasIndex(x => x.FunctionsDispatchedAt);
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Actor).HasMaxLength(128);
            e.Property(x => x.Action).HasMaxLength(128);
            e.Property(x => x.Resource).HasMaxLength(256);
            e.Property(x => x.Ip).HasMaxLength(64);
            // The read surface's one real query: a project's entries newest-first, optionally
            // narrowed by actor/action/resource. Postgres scans a leading-columns btree backwards
            // just as fast as forwards, so this single composite covers "newest first" without a
            // separate DESC index; the narrowing filters apply as a bounded scan within the project,
            // not a second index, which is enough at this table's scale.
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
        });

        b.Entity<WebhookSubscription>(e =>
        {
            e.ToTable("webhook_subscriptions");
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.Secret).HasMaxLength(256);
            e.Property(x => x.DisabledReason).HasMaxLength(256);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProjectId);
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries");
            e.Property(x => x.EventType).HasMaxLength(256);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(2048);
            e.HasOne<WebhookSubscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SubscriptionId);
            // The delivery worker's claim query: due, unfinished deliveries, oldest first.
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });

        b.Entity<WebhookDeliveryAttempt>(e =>
        {
            e.ToTable("webhook_delivery_attempts");
            e.Property(x => x.ResponseBody).HasMaxLength(8192);
            e.Property(x => x.Error).HasMaxLength(2048);
            e.HasOne<WebhookDelivery>().WithMany().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.DeliveryId);
        });

        b.Entity<FunctionDef>(e =>
        {
            e.ToTable("functions");
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Runtime).HasMaxLength(16);
            e.Property(x => x.Entrypoint).HasMaxLength(256);
            e.Property(x => x.Schedule).HasMaxLength(64);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Key }).IsUnique();
            // FunctionScheduler's claim query: due, enabled, scheduled functions.
            e.HasIndex(x => x.NextScheduledRunAt);
        });

        b.Entity<FunctionEnvVar>(e =>
        {
            e.ToTable("function_env_vars");
            e.Property(x => x.Key).HasMaxLength(256);
            e.Property(x => x.ProtectedValue).HasMaxLength(8192);
            e.HasOne<FunctionDef>().WithMany().HasForeignKey(x => x.FunctionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.FunctionId, x.Key }).IsUnique();
        });

        b.Entity<FunctionDeployment>(e =>
        {
            e.ToTable("function_deployments");
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(4096);
            e.Property(x => x.ImageTag).HasMaxLength(256);
            e.HasOne<FunctionDef>().WithMany().HasForeignKey(x => x.FunctionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.FunctionId);
            // FunctionBuildWorker's claim query: queued builds, oldest first.
            e.HasIndex(x => x.Status);
        });

        b.Entity<FunctionDeploymentSource>(e =>
        {
            e.ToTable("function_deployment_sources");
            e.HasKey(x => x.DeploymentId);
            e.Property(x => x.Tar).HasColumnType("bytea");
            e.HasOne<FunctionDeployment>().WithOne().HasForeignKey<FunctionDeploymentSource>(x => x.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FunctionExecution>(e =>
        {
            e.ToTable("function_executions");
            e.Property(x => x.Trigger).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Method).HasMaxLength(16);
            e.Property(x => x.Path).HasMaxLength(2048);
            e.Property(x => x.Errors).HasMaxLength(65536);
            // "event:<type>" carries a full event type string (up to three 32-char hex ids plus
            // separators) — comfortably longer than the 128 a user:<id>/schedule/console/key/guest
            // value would need alone.
            e.Property(x => x.TriggeredBy).HasMaxLength(300);
            e.HasOne<FunctionDef>().WithMany().HasForeignKey(x => x.FunctionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.FunctionId);
            // FunctionExecutionWorker's claim query: waiting async executions, oldest first.
            e.HasIndex(x => x.Status);
        });

        b.Entity<Site>(e =>
        {
            e.ToTable("sites");
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.RootDirectory).HasMaxLength(256);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Key }).IsUnique();
        });

        b.Entity<SiteEnvVar>(e =>
        {
            e.ToTable("site_env_vars");
            e.Property(x => x.Key).HasMaxLength(256);
            e.Property(x => x.ProtectedValue).HasMaxLength(8192);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SiteId, x.Key }).IsUnique();
        });

        b.Entity<SiteDeployment>(e =>
        {
            e.ToTable("site_deployments");
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(4096);
            e.Property(x => x.ImageTag).HasMaxLength(256);
            e.Property(x => x.ContainerId).HasMaxLength(128);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SiteId);
            // SiteBuildWorker's claim query: queued builds, oldest first.
            e.HasIndex(x => x.Status);
            // SiteScreenshotWorker's claim query: activated deployments still missing a screenshot.
            e.HasIndex(x => new { x.ActivatedAt, x.ScreenshotCapturedAt });
        });

        b.Entity<SiteDeploymentSource>(e =>
        {
            e.ToTable("site_deployment_sources");
            e.HasKey(x => x.DeploymentId);
            e.Property(x => x.Tar).HasColumnType("bytea");
            e.HasOne<SiteDeployment>().WithOne().HasForeignKey<SiteDeploymentSource>(x => x.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SiteDeploymentScreenshot>(e =>
        {
            e.ToTable("site_deployment_screenshots");
            e.HasKey(x => x.DeploymentId);
            e.Property(x => x.Png).HasColumnType("bytea");
            e.HasOne<SiteDeployment>().WithOne().HasForeignKey<SiteDeploymentScreenshot>(x => x.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MessagingProvider>(e =>
        {
            e.ToTable("messaging_providers");
            e.Property(x => x.Type).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Config).HasColumnType("jsonb");
            e.Property(x => x.ProtectedSecret).HasMaxLength(2048);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            // EmailProviderResolver's lookup: the enabled default provider of a given type.
            e.HasIndex(x => new { x.ProjectId, x.Type, x.Enabled, x.IsDefault });
        });

        b.Entity<MessagingTopic>(e =>
        {
            e.ToTable("messaging_topics");
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Description).HasMaxLength(1024);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Key }).IsUnique();
        });

        b.Entity<MessagingTarget>(e =>
        {
            e.ToTable("messaging_targets");
            e.Property(x => x.Type).HasMaxLength(16);
            e.Property(x => x.Identifier).HasMaxLength(320);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Type }).IsUnique();
        });

        b.Entity<MessagingSubscriber>(e =>
        {
            e.ToTable("messaging_subscribers");
            e.HasOne<MessagingTopic>().WithMany().HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MessagingTarget>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TopicId, x.TargetId }).IsUnique();
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.Property(x => x.Type).HasMaxLength(16);
            e.Property(x => x.Subject).HasMaxLength(998);
            e.Property(x => x.Body).HasMaxLength(65536);
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProjectId);
        });

        b.Entity<MessageTarget>(e =>
        {
            e.ToTable("message_targets");
            e.Property(x => x.Identifier).HasMaxLength(320);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(2048);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.MessageId);
            // MessageSendWorker's claim query: queued targets, oldest first.
            e.HasIndex(x => x.Status);
        });

        b.Entity<MessagingTemplate>(e =>
        {
            e.ToTable("messaging_templates");
            e.Property(x => x.Channel).HasMaxLength(16);
            e.Property(x => x.Key).HasMaxLength(32);
            e.Property(x => x.Subject).HasMaxLength(998);
            e.Property(x => x.Body).HasMaxLength(65536);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Channel, x.Key }).IsUnique();
        });
    }
}
