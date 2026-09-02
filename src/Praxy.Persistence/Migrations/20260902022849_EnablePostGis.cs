using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <summary>
    /// No EF model change — <c>geo</c> is "another string-typed column/index Type value," not a new
    /// entity or column (docs/research/geo-nearby.md). This is the actual dependency the feature
    /// needs: PostGIS, activated once per database via the same pg_advisory_lock-guarded
    /// CatalogMigrator startup path every other migration already runs through, no new mechanism.
    /// Verified against a real postgis/postgis:17-3.6-alpine container: the self-host compose's
    /// `praxy` role is created as a superuser by the official postgres image's own entrypoint (via
    /// initdb --username), so CREATE EXTENSION succeeds without any extra grant.
    /// </summary>
    public partial class EnablePostGis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS postgis;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort only: fails with a dependency error if any geo column/index still exists,
            // same as Postgres's own DROP EXTENSION behavior — migrations are forward-only in
            // practice anyway (docs/self-host.md's Upgrading section), so this is a courtesy, not a
            // guarantee.
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS postgis;");
        }
    }
}
