using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Functions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "dispatched_at",
                schema: "praxy",
                table: "events",
                newName: "webhooks_dispatched_at");

            migrationBuilder.RenameIndex(
                name: "ix_events_dispatched_at",
                schema: "praxy",
                table: "events",
                newName: "ix_events_webhooks_dispatched_at");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "functions_dispatched_at",
                schema: "praxy",
                table: "events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "functions",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    runtime = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    entrypoint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    events = table.Column<string[]>(type: "text[]", nullable: false),
                    schedule = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    next_scheduled_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    active_deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_functions", x => x.id);
                    table.ForeignKey(
                        name: "fk_functions_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "function_deployments",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    function_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    source_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    build_log = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    image_tag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_function_deployments", x => x.id);
                    table.ForeignKey(
                        name: "fk_function_deployments_functions_function_id",
                        column: x => x.function_id,
                        principalSchema: "praxy",
                        principalTable: "functions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "function_env_vars",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    function_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    protected_value = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_function_env_vars", x => x.id);
                    table.ForeignKey(
                        name: "fk_function_env_vars_functions_function_id",
                        column: x => x.function_id,
                        principalSchema: "praxy",
                        principalTable: "functions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "function_executions",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    function_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    async = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    request_body = table.Column<string>(type: "text", nullable: true),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    logs = table.Column<string>(type: "text", nullable: false),
                    errors = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    cold_start = table.Column<bool>(type: "boolean", nullable: false),
                    triggered_by = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_function_executions", x => x.id);
                    table.ForeignKey(
                        name: "fk_function_executions_functions_function_id",
                        column: x => x.function_id,
                        principalSchema: "praxy",
                        principalTable: "functions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "function_deployment_sources",
                schema: "praxy",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tar = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_function_deployment_sources", x => x.deployment_id);
                    table.ForeignKey(
                        name: "fk_function_deployment_sources_function_deployments_deployment",
                        column: x => x.deployment_id,
                        principalSchema: "praxy",
                        principalTable: "function_deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_functions_dispatched_at",
                schema: "praxy",
                table: "events",
                column: "functions_dispatched_at");

            migrationBuilder.CreateIndex(
                name: "ix_function_deployments_function_id",
                schema: "praxy",
                table: "function_deployments",
                column: "function_id");

            migrationBuilder.CreateIndex(
                name: "ix_function_deployments_status",
                schema: "praxy",
                table: "function_deployments",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_function_env_vars_function_id_key",
                schema: "praxy",
                table: "function_env_vars",
                columns: new[] { "function_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_function_executions_function_id",
                schema: "praxy",
                table: "function_executions",
                column: "function_id");

            migrationBuilder.CreateIndex(
                name: "ix_function_executions_status",
                schema: "praxy",
                table: "function_executions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_functions_next_scheduled_run_at",
                schema: "praxy",
                table: "functions",
                column: "next_scheduled_run_at");

            migrationBuilder.CreateIndex(
                name: "ix_functions_project_id_key",
                schema: "praxy",
                table: "functions",
                columns: new[] { "project_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "function_deployment_sources",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "function_env_vars",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "function_executions",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "function_deployments",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "functions",
                schema: "praxy");

            migrationBuilder.DropIndex(
                name: "ix_events_functions_dispatched_at",
                schema: "praxy",
                table: "events");

            migrationBuilder.DropColumn(
                name: "functions_dispatched_at",
                schema: "praxy",
                table: "events");

            migrationBuilder.RenameColumn(
                name: "webhooks_dispatched_at",
                schema: "praxy",
                table: "events",
                newName: "dispatched_at");

            migrationBuilder.RenameIndex(
                name: "ix_events_webhooks_dispatched_at",
                schema: "praxy",
                table: "events",
                newName: "ix_events_dispatched_at");
        }
    }
}
