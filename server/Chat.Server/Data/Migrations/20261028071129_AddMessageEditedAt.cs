using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageEditedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "edited_at",
                table: "messages",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "edited_at",
                table: "messages");
        }
    }
}
