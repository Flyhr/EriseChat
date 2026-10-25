using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageFileId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "file_id",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_messages_file_id",
                table: "messages",
                column: "file_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_messages_file_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "file_id",
                table: "messages");
        }
    }
}
