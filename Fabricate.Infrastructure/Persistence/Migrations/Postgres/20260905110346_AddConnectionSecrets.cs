using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fabricate.Infrastructure.Persistence.Migrations.Postgres
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
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "Connections",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "KeyVersion",
                table: "Connections",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "LastValidatedAt",
                table: "Connections",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastValidationError",
                table: "Connections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Redacted",
                table: "Connections",
                type: "text",
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
