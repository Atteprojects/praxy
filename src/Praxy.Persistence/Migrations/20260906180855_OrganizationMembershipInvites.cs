using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationMembershipInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfills every pre-existing row as confirmed: Phase 1 never had an unconfirmed
            // membership (every org has exactly one member, its creator), so this is not a
            // guess — it is the only value that can be true for a row that predates invites.
            migrationBuilder.AddColumn<bool>(
                name: "confirmed",
                schema: "praxy",
                table: "organization_members",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "invited_at",
                schema: "praxy",
                table: "organization_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "secret_hash",
                schema: "praxy",
                table: "organization_members",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "confirmed",
                schema: "praxy",
                table: "organization_members");

            migrationBuilder.DropColumn(
                name: "invited_at",
                schema: "praxy",
                table: "organization_members");

            migrationBuilder.DropColumn(
                name: "secret_hash",
                schema: "praxy",
                table: "organization_members");
        }
    }
}
