using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FunctionsGitIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "production_branch",
                schema: "praxy",
                table: "functions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_full_name",
                schema: "praxy",
                table: "functions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "branch",
                schema: "praxy",
                table: "function_deployments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "commit_message",
                schema: "praxy",
                table: "function_deployments",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "commit_sha",
                schema: "praxy",
                table: "function_deployments",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            // Existing rows all predate git-sourced deployments, so they backfill as "upload" — the
            // value application code already writes for every tar-based deployment going forward.
            // A bare "" default here would violate ck_function_deployments_source below for every
            // pre-existing row.
            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "praxy",
                table: "function_deployments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "upload");

            migrationBuilder.AddCheckConstraint(
                name: "ck_function_deployments_source",
                schema: "praxy",
                table: "function_deployments",
                sql: "source in ('upload', 'git')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_function_deployments_source",
                schema: "praxy",
                table: "function_deployments");

            migrationBuilder.DropColumn(
                name: "production_branch",
                schema: "praxy",
                table: "functions");

            migrationBuilder.DropColumn(
                name: "repository_full_name",
                schema: "praxy",
                table: "functions");

            migrationBuilder.DropColumn(
                name: "branch",
                schema: "praxy",
                table: "function_deployments");

            migrationBuilder.DropColumn(
                name: "commit_message",
                schema: "praxy",
                table: "function_deployments");

            migrationBuilder.DropColumn(
                name: "commit_sha",
                schema: "praxy",
                table: "function_deployments");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "praxy",
                table: "function_deployments");
        }
    }
}
