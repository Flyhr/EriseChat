using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "friend_requests",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    from_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    to_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    message = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'pending'"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    handled_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friend_requests", x => x.id);
                    table.CheckConstraint("ck_friend_requests_status", "status IN ('pending', 'accepted', 'rejected', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_friend_requests_users_from_user_id",
                        column: x => x.from_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_friend_requests_users_to_user_id",
                        column: x => x.to_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "friends",
                columns: table => new
                {
                    owner_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    friend_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    remark = table.Column<string>(type: "TEXT", nullable: true),
                    group_name = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friends", x => new { x.owner_user_id, x.friend_user_id });
                    table.ForeignKey(
                        name: "FK_friends_users_friend_user_id",
                        column: x => x.friend_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_friends_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_friend_requests_from_status",
                table: "friend_requests",
                columns: new[] { "from_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "idx_friend_requests_to_status",
                table: "friend_requests",
                columns: new[] { "to_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_friend_requests_pending",
                table: "friend_requests",
                columns: new[] { "from_user_id", "to_user_id" },
                unique: true,
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "idx_friends_friend",
                table: "friends",
                column: "friend_user_id");

            migrationBuilder.CreateIndex(
                name: "idx_friends_owner",
                table: "friends",
                column: "owner_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "friend_requests");

            migrationBuilder.DropTable(
                name: "friends");
        }
    }
}
