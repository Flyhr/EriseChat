using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAllowStrangerMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_stranger_messages",
                table: "users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allow_stranger_messages",
                table: "users");
        }
    }
}
