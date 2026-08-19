using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <summary>
    /// Adds the function <c>execute</c> role list. Existing functions backfill to <c>{}</c> —
    /// deny — which is a DELIBERATE BREAKING CHANGE on upgrade: before this migration any caller
    /// holding a project id could invoke any enabled function, so preserving that behaviour would
    /// have migrated the hole forward silently. After upgrading, grant each function the roles it
    /// needs (console: Functions → Settings → Execute access). The console's own invoke, event
    /// triggers and cron runs are unaffected.
    /// </summary>
    public partial class FunctionExecutePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "execute",
                schema: "praxy",
                table: "functions",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "execute",
                schema: "praxy",
                table: "functions");
        }
    }
}
