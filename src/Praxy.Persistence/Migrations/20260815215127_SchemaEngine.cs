using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SchemaEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "pid",
                schema: "praxy",
                table: "schema_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "table_id",
                schema: "praxy",
                table: "schema_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "databases",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    schema_name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_databases", x => x.id);
                    table.ForeignKey(
                        name: "fk_databases_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tables",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    database_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    physical_name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    row_security = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tables", x => x.id);
                    table.ForeignKey(
                        name: "fk_tables_databases_database_id",
                        column: x => x.database_id,
                        principalSchema: "praxy",
                        principalTable: "databases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "columns",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    physical_name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    is_array = table.Column<bool>(type: "boolean", nullable: false),
                    size = table.Column<int>(type: "integer", nullable: true),
                    default_value = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_columns", x => x.id);
                    table.ForeignKey(
                        name: "fk_columns_tables_table_id",
                        column: x => x.table_id,
                        principalSchema: "praxy",
                        principalTable: "tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "indexes",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    columns = table.Column<string[]>(type: "text[]", nullable: false),
                    orders = table.Column<string[]>(type: "text[]", nullable: false),
                    physical_name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_indexes", x => x.id);
                    table.ForeignKey(
                        name: "fk_indexes_tables_table_id",
                        column: x => x.table_id,
                        principalSchema: "praxy",
                        principalTable: "tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "table_permissions",
                schema: "praxy",
                columns: table => new
                {
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_table_permissions", x => new { x.table_id, x.action, x.role });
                    table.ForeignKey(
                        name: "fk_table_permissions_tables_table_id",
                        column: x => x.table_id,
                        principalSchema: "praxy",
                        principalTable: "tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_schema_jobs_table_id_status",
                schema: "praxy",
                table: "schema_jobs",
                columns: new[] { "table_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_columns_table_id_key",
                schema: "praxy",
                table: "columns",
                columns: new[] { "table_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_databases_project_id_key",
                schema: "praxy",
                table: "databases",
                columns: new[] { "project_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_indexes_table_id_key",
                schema: "praxy",
                table: "indexes",
                columns: new[] { "table_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tables_database_id_key",
                schema: "praxy",
                table: "tables",
                columns: new[] { "database_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "columns",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "indexes",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "table_permissions",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "tables",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "databases",
                schema: "praxy");

            migrationBuilder.DropIndex(
                name: "ix_schema_jobs_table_id_status",
                schema: "praxy",
                table: "schema_jobs");

            migrationBuilder.DropColumn(
                name: "pid",
                schema: "praxy",
                table: "schema_jobs");

            migrationBuilder.DropColumn(
                name: "table_id",
                schema: "praxy",
                table: "schema_jobs");
        }
    }
}
