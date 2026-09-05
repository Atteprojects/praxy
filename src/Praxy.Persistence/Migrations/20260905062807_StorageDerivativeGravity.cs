using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageDerivativeGravity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_file_derivatives_file_id_width_height_format_quality",
                schema: "praxy",
                table: "file_derivatives");

            // "center" for pre-existing rows, not "" — every derivative generated before this
            // migration was cropped centered (the only behavior that existed), so this is the true
            // value for that data, and it's what a post-migration request for the same crop will
            // resolve to. Any other default would silently orphan every existing cropped derivative:
            // the cache key would never match again, and each would regenerate once for no reason.
            migrationBuilder.AddColumn<string>(
                name: "gravity",
                schema: "praxy",
                table: "file_derivatives",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "center");

            migrationBuilder.CreateIndex(
                name: "ix_file_derivatives_file_id_width_height_format_quality_gravity",
                schema: "praxy",
                table: "file_derivatives",
                columns: new[] { "file_id", "width", "height", "format", "quality", "gravity" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_file_derivatives_file_id_width_height_format_quality_gravity",
                schema: "praxy",
                table: "file_derivatives");

            migrationBuilder.DropColumn(
                name: "gravity",
                schema: "praxy",
                table: "file_derivatives");

            migrationBuilder.CreateIndex(
                name: "ix_file_derivatives_file_id_width_height_format_quality",
                schema: "praxy",
                table: "file_derivatives",
                columns: new[] { "file_id", "width", "height", "format", "quality" },
                unique: true);
        }
    }
}
