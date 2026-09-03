using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Storage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "buckets",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    file_security = table.Column<bool>(type: "boolean", nullable: false),
                    max_file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    allowed_mime_types = table.Column<string[]>(type: "text[]", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_buckets", x => x.id);
                    table.ForeignKey(
                        name: "fk_buckets_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "bucket_permissions",
                schema: "praxy",
                columns: table => new
                {
                    bucket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bucket_permissions", x => new { x.bucket_id, x.action, x.role });
                    table.ForeignKey(
                        name: "fk_bucket_permissions_buckets_bucket_id",
                        column: x => x.bucket_id,
                        principalSchema: "praxy",
                        principalTable: "buckets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "files",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    chunk_size_bytes = table.Column<int>(type: "integer", nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_files", x => x.id);
                    table.ForeignKey(
                        name: "fk_files_buckets_bucket_id",
                        column: x => x.bucket_id,
                        principalSchema: "praxy",
                        principalTable: "buckets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_chunks",
                schema: "praxy",
                columns: table => new
                {
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_chunks", x => new { x.file_id, x.index });
                    table.ForeignKey(
                        name: "fk_file_chunks_files_file_id",
                        column: x => x.file_id,
                        principalSchema: "praxy",
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_buckets_project_id_key",
                schema: "praxy",
                table: "buckets",
                columns: new[] { "project_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_files_bucket_id_created_at",
                schema: "praxy",
                table: "files",
                columns: new[] { "bucket_id", "created_at" });

            // Not expressible through the EF model, and not cosmetic: bytea defaults to STORAGE
            // EXTENDED, which attempts LZ compression on every value before storing it out of
            // line. Chunks hold user media — JPEG/PNG/MP4/ZIP are already compressed, so that pass
            // is CPU spent for nothing (docs/research/storage.md). EXTERNAL keeps values out of
            // line and skips the compression attempt. Invisible from behavior alone, so
            // StorageEngineTests asserts pg_attribute.attstorage = 'e' rather than trusting this
            // line ran.
            migrationBuilder.Sql(
                """ALTER TABLE praxy.file_chunks ALTER COLUMN "data" SET STORAGE EXTERNAL""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bucket_permissions",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "file_chunks",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "files",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "buckets",
                schema: "praxy");
        }
    }
}
