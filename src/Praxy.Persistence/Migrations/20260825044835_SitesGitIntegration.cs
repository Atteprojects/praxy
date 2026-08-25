using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SitesGitIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "production_branch",
                schema: "praxy",
                table: "sites",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_full_name",
                schema: "praxy",
                table: "sites",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "branch",
                schema: "praxy",
                table: "site_deployments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "commit_message",
                schema: "praxy",
                table: "site_deployments",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "commit_sha",
                schema: "praxy",
                table: "site_deployments",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            // Existing rows all predate git-sourced deployments, so they backfill as "upload" — the
            // value application code already writes for every tar-based deployment going forward.
            // A bare "" default here would violate ck_site_deployments_source below for every
            // pre-existing row.
            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "praxy",
                table: "site_deployments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "upload");

            migrationBuilder.CreateTable(
                name: "vcs_installations",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    account_login = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vcs_installations", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_site_deployments_source",
                schema: "praxy",
                table: "site_deployments",
                sql: "source in ('upload', 'git')");

            migrationBuilder.CreateIndex(
                name: "ix_vcs_installations_installation_id",
                schema: "praxy",
                table: "vcs_installations",
                column: "installation_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vcs_installations",
                schema: "praxy");

            migrationBuilder.DropCheckConstraint(
                name: "ck_site_deployments_source",
                schema: "praxy",
                table: "site_deployments");

            migrationBuilder.DropColumn(
                name: "production_branch",
                schema: "praxy",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "repository_full_name",
                schema: "praxy",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "branch",
                schema: "praxy",
                table: "site_deployments");

            migrationBuilder.DropColumn(
                name: "commit_message",
                schema: "praxy",
                table: "site_deployments");

            migrationBuilder.DropColumn(
                name: "commit_sha",
                schema: "praxy",
                table: "site_deployments");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "praxy",
                table: "site_deployments");
        }
    }
}
