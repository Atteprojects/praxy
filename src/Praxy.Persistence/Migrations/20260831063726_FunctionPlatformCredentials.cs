using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FunctionPlatformCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "platform_api_key_id",
                schema: "praxy",
                table: "functions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "platform_api_key_secret_protected",
                schema: "praxy",
                table: "functions",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "platform_scopes",
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
                name: "platform_api_key_id",
                schema: "praxy",
                table: "functions");

            migrationBuilder.DropColumn(
                name: "platform_api_key_secret_protected",
                schema: "praxy",
                table: "functions");

            migrationBuilder.DropColumn(
                name: "platform_scopes",
                schema: "praxy",
                table: "functions");
        }
    }
}
