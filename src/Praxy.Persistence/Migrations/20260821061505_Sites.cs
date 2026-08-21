using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Sites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sites",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    root_directory = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    active_deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sites", x => x.id);
                    table.ForeignKey(
                        name: "fk_sites_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "site_deployments",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    source_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    build_log = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    image_tag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    container_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_deployments", x => x.id);
                    table.ForeignKey(
                        name: "fk_site_deployments_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "praxy",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "site_env_vars",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    protected_value = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_env_vars", x => x.id);
                    table.ForeignKey(
                        name: "fk_site_env_vars_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "praxy",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "site_deployment_sources",
                schema: "praxy",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tar = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_deployment_sources", x => x.deployment_id);
                    table.ForeignKey(
                        name: "fk_site_deployment_sources_site_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalSchema: "praxy",
                        principalTable: "site_deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_site_deployments_site_id",
                schema: "praxy",
                table: "site_deployments",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_site_deployments_status",
                schema: "praxy",
                table: "site_deployments",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_site_env_vars_site_id_key",
                schema: "praxy",
                table: "site_env_vars",
                columns: new[] { "site_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sites_project_id_key",
                schema: "praxy",
                table: "sites",
                columns: new[] { "project_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_deployment_sources",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "site_env_vars",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "site_deployments",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "sites",
                schema: "praxy");
        }
    }
}
