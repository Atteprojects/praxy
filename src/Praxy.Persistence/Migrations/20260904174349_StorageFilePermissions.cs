using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageFilePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "inline_types",
                schema: "praxy",
                table: "buckets",
                type: "text[]",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "file_permissions",
                schema: "praxy",
                columns: table => new
                {
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_permissions", x => new { x.file_id, x.action, x.role });
                    table.ForeignKey(
                        name: "fk_file_permissions_files_file_id",
                        column: x => x.file_id,
                        principalSchema: "praxy",
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_file_permissions_action_role",
                schema: "praxy",
                table: "file_permissions",
                columns: new[] { "action", "role" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_permissions",
                schema: "praxy");

            migrationBuilder.DropColumn(
                name: "inline_types",
                schema: "praxy",
                table: "buckets");
        }
    }
}
