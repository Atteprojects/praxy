using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <summary>
    /// No schema change — <c>scopes</c> stays <c>text[]</c>. Renames the stored scope string itself:
    /// any key holding the old <c>functions.execute</c> now holds <c>execution.write</c>, so a key
    /// issued before this migration keeps working exactly as it did, without an operator having to
    /// notice and re-grant anything.
    /// </summary>
    public partial class RenameFunctionsExecuteScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE praxy.api_keys
                SET scopes = array_replace(scopes, 'functions.execute', 'execution.write')
                WHERE 'functions.execute' = ANY(scopes);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE praxy.api_keys
                SET scopes = array_replace(scopes, 'execution.write', 'functions.execute')
                WHERE 'execution.write' = ANY(scopes);
                """);
        }
    }
}
