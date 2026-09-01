using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RelationshipColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "target_table_id",
                schema: "praxy",
                table: "columns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_columns_target_table_id",
                schema: "praxy",
                table: "columns",
                column: "target_table_id");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_columns_tables_target_table_id",
                schema: "praxy",
                table: "columns");

            migrationBuilder.DropIndex(
                name: "ix_columns_target_table_id",
                schema: "praxy",
                table: "columns");

            migrationBuilder.DropColumn(
                name: "target_table_id",
                schema: "praxy",
                table: "columns");
        }
    }
}
