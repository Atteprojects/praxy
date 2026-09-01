using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RelationshipColumnTargetSetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_columns_tables_target_table_id",
                schema: "praxy",
                table: "columns");

            migrationBuilder.AddForeignKey(
                name: "fk_columns_tables_target_table_id",
                schema: "praxy",
                table: "columns",
                column: "target_table_id",
                principalSchema: "praxy",
                principalTable: "tables",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_columns_tables_target_table_id",
                schema: "praxy",
                table: "columns");

            migrationBuilder.AddForeignKey(
                name: "fk_columns_tables_target_table_id",
                schema: "praxy",
                table: "columns",
                column: "target_table_id",
                principalSchema: "praxy",
                principalTable: "tables",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
