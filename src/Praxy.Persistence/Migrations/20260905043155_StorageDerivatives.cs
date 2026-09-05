using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageDerivatives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "height",
                schema: "praxy",
                table: "files",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "width",
                schema: "praxy",
                table: "files",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "file_derivatives",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quality = table.Column<int>(type: "integer", nullable: false),
                    mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    chunk_size_bytes = table.Column<int>(type: "integer", nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_derivatives", x => x.id);
                    table.ForeignKey(
                        name: "fk_file_derivatives_files_file_id",
                        column: x => x.file_id,
                        principalSchema: "praxy",
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_derivative_chunks",
                schema: "praxy",
                columns: table => new
                {
                    derivative_id = table.Column<Guid>(type: "uuid", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_derivative_chunks", x => new { x.derivative_id, x.index });
                    table.ForeignKey(
                        name: "fk_file_derivative_chunks_file_derivatives_derivative_id",
                        column: x => x.derivative_id,
                        principalSchema: "praxy",
                        principalTable: "file_derivatives",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_file_derivatives_file_id_width_height_format_quality",
                schema: "praxy",
                table: "file_derivatives",
                columns: new[] { "file_id", "width", "height", "format", "quality" },
                unique: true);

            // Same reasoning as file_chunks.data (see the Storage migration): the default EXTENDED
            // storage strategy tries to LZ-compress every value before storing it out of line, which
            // is pure CPU burn for an already-compressed encoded image.
            migrationBuilder.Sql(
                """ALTER TABLE praxy.file_derivative_chunks ALTER COLUMN "data" SET STORAGE EXTERNAL""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_derivative_chunks",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "file_derivatives",
                schema: "praxy");

            migrationBuilder.DropColumn(
                name: "height",
                schema: "praxy",
                table: "files");

            migrationBuilder.DropColumn(
                name: "width",
                schema: "praxy",
                table: "files");
        }
    }
}
