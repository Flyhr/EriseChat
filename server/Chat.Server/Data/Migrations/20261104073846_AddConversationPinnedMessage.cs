using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationPinnedMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pinned_message_id",
                table: "conversations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pinned_message_id",
                table: "conversations");
        }
    }
}
