using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupInviteLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_invite_links",
                columns: table => new
                {
                    token = table.Column<string>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_by = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revoked_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_invite_links", x => x.token);
                    table.ForeignKey(
                        name: "FK_group_invite_links_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_invite_links_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_group_invite_links_conversation",
                table: "group_invite_links",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_invite_links_created_by",
                table: "group_invite_links",
                column: "created_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_invite_links");
        }
    }
}
