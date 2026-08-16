using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praxy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Messaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "messages",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    body = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    topic_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    user_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_messages_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messaging_providers",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    protected_secret = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messaging_providers", x => x.id);
                    table.ForeignKey(
                        name: "fk_messaging_providers_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messaging_targets",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    identifier = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messaging_targets", x => x.id);
                    table.ForeignKey(
                        name: "fk_messaging_targets_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_messaging_targets_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "praxy",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messaging_templates",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    body = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messaging_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_messaging_templates_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messaging_topics",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messaging_topics", x => x.id);
                    table.ForeignKey(
                        name: "fk_messaging_topics_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "praxy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_targets",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identifier = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_targets", x => x.id);
                    table.ForeignKey(
                        name: "fk_message_targets_messages_message_id",
                        column: x => x.message_id,
                        principalSchema: "praxy",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messaging_subscribers",
                schema: "praxy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messaging_subscribers", x => x.id);
                    table.ForeignKey(
                        name: "fk_messaging_subscribers_messaging_targets_target_id",
                        column: x => x.target_id,
                        principalSchema: "praxy",
                        principalTable: "messaging_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_messaging_subscribers_messaging_topics_topic_id",
                        column: x => x.topic_id,
                        principalSchema: "praxy",
                        principalTable: "messaging_topics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_message_targets_message_id",
                schema: "praxy",
                table: "message_targets",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_message_targets_status",
                schema: "praxy",
                table: "message_targets",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_messages_project_id",
                schema: "praxy",
                table: "messages",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_providers_project_id_type_enabled_is_default",
                schema: "praxy",
                table: "messaging_providers",
                columns: new[] { "project_id", "type", "enabled", "is_default" });

            migrationBuilder.CreateIndex(
                name: "ix_messaging_subscribers_target_id",
                schema: "praxy",
                table: "messaging_subscribers",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_subscribers_topic_id_target_id",
                schema: "praxy",
                table: "messaging_subscribers",
                columns: new[] { "topic_id", "target_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messaging_targets_project_id",
                schema: "praxy",
                table: "messaging_targets",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_targets_user_id_type",
                schema: "praxy",
                table: "messaging_targets",
                columns: new[] { "user_id", "type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messaging_templates_project_id_channel_key",
                schema: "praxy",
                table: "messaging_templates",
                columns: new[] { "project_id", "channel", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messaging_topics_project_id_key",
                schema: "praxy",
                table: "messaging_topics",
                columns: new[] { "project_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_targets",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "messaging_providers",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "messaging_subscribers",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "messaging_templates",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "messaging_targets",
                schema: "praxy");

            migrationBuilder.DropTable(
                name: "messaging_topics",
                schema: "praxy");
        }
    }
}
