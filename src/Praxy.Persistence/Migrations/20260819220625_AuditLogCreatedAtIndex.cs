using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditLogCreatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_log_project_id",
                schema: "praxy",
                table: "audit_log");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_project_id_created_at",
                schema: "praxy",
                table: "audit_log",
                columns: new[] { "project_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_log_project_id_created_at",
                schema: "praxy",
                table: "audit_log");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_project_id",
                schema: "praxy",
                table: "audit_log",
                column: "project_id");
        }
    }
}
