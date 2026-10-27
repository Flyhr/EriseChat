using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExtendEmailCodePurpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_email_codes_purpose",
                table: "email_codes");

            migrationBuilder.AddCheckConstraint(
                name: "ck_email_codes_purpose",
                table: "email_codes",
                sql: "purpose IN ('register', 'login', 'reset')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_email_codes_purpose",
                table: "email_codes");

            migrationBuilder.AddCheckConstraint(
                name: "ck_email_codes_purpose",
                table: "email_codes",
                sql: "purpose IN ('register', 'login')");
        }
    }
}
