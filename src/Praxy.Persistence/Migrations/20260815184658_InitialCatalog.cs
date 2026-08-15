using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "praxy");

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: true),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    resource = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "schema_jobs",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    database_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schema_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    last_ping_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                    table.ForeignKey(
                        name: "fk_projects_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "praxy",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    secret_hash = table.Column<string>(type: "text", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                    table.CheckConstraint("ck_api_keys_project_not_console", "project_id <> 'console'");
                    table.ForeignKey(
                        name: "fk_api_keys_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "platforms",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    hostname = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platforms", x => x.id);
                    table.ForeignKey(
                        name: "fk_platforms_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    email_verified = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<bool>(type: "boolean", nullable: false),
                    labels = table.Column<string[]>(type: "text[]", nullable: false),
                    prefs = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_members",
                schema: "praxy",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_members", x => new { x.organization_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_organization_members_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "praxy",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_organization_members_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "praxy",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    mfa_verified = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_sessions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "praxy",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_project_id",
                schema: "praxy",
                table: "api_keys",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_project_id",
                schema: "praxy",
                table: "audit_log",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_created_at",
                schema: "praxy",
                table: "events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_organization_members_user_id",
                schema: "praxy",
                table: "organization_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_platforms_project_id",
                schema: "praxy",
                table: "platforms",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_organization_id",
                schema: "praxy",
                table: "projects",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_schema_jobs_database_id_status",
                schema: "praxy",
                table: "schema_jobs",
                columns: new[] { "database_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_user_id",
                schema: "praxy",
                table: "sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_project_id_email",
                schema: "praxy",
                table: "users",
                columns: new[] { "project_id", "email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "events",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "organization_members",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "platforms",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "schema_jobs",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "users",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "organizations",
                schema: "praxy");
        }
    }
}
