using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSiteScreenshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_deployment_screenshots",
                schema: "praxy");

            migrationBuilder.DropIndex(
                name: "ix_site_deployments_activated_at_screenshot_captured_at",
                schema: "praxy",
                table: "site_deployments");

            migrationBuilder.DropColumn(
                name: "screenshot_attempts",
                schema: "praxy",
                table: "site_deployments");

            migrationBuilder.DropColumn(
                name: "screenshot_captured_at",
                schema: "praxy",
                table: "site_deployments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "screenshot_attempts",
                schema: "praxy",
                table: "site_deployments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "screenshot_captured_at",
                schema: "praxy",
                table: "site_deployments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "site_deployment_screenshots",
                schema: "praxy",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    png = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_deployment_screenshots", x => x.deployment_id);
                    table.ForeignKey(
                        name: "fk_site_deployment_screenshots_site_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalSchema: "praxy",
                        principalTable: "site_deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_site_deployments_activated_at_screenshot_captured_at",
                schema: "praxy",
                table: "site_deployments",
                columns: new[] { "activated_at", "screenshot_captured_at" });
        }
    }
}
