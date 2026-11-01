using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceMessageType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_msg_type",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_msg_type",
                table: "messages",
                sql: "msg_type IN ('text', 'image', 'file', 'voice', 'system')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_msg_type",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_msg_type",
                table: "messages",
                sql: "msg_type IN ('text', 'image', 'file', 'system')");
        }
    }
}
