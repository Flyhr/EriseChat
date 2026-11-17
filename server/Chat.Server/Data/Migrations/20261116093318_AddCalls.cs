using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calls",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<string>(type: "TEXT", nullable: false),
                    caller_id = table.Column<string>(type: "TEXT", nullable: false),
                    callee_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    answered_at = table.Column<long>(type: "INTEGER", nullable: true),
                    connected_at = table.Column<long>(type: "INTEGER", nullable: true),
                    ended_at = table.Column<long>(type: "INTEGER", nullable: true),
                    end_reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calls", x => x.id);
                    table.CheckConstraint("ck_calls_kind", "kind IN ('audio', 'video')");
                    table.CheckConstraint("ck_calls_state", "state IN ('ringing', 'connecting', 'active', 'ended')");
                    table.ForeignKey(
                        name: "FK_calls_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_calls_users_callee_id",
                        column: x => x.callee_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_calls_users_caller_id",
                        column: x => x.caller_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_calls_callee_state",
                table: "calls",
                columns: new[] { "callee_id", "state" });

            migrationBuilder.CreateIndex(
                name: "idx_calls_conversation_created",
                table: "calls",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_calls_caller_id",
                table: "calls",
                column: "caller_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calls");
        }
    }
}
