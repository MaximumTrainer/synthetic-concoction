using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fabricate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CipherText",
                table: "Connections",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "Connections",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "KeyVersion",
                table: "Connections",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "LastValidatedAt",
                table: "Connections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastValidationError",
                table: "Connections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Redacted",
                table: "Connections",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CipherText",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "KeyVersion",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "LastValidatedAt",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "LastValidationError",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "Redacted",
                table: "Connections");
        }
    }
}
