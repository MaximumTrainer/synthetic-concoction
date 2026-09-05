using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fabricate.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddPersonalLlmCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowPersonalCredentials",
                table: "WorkspaceLlmPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "LlmCredentials",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "LlmCredentials",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowPersonalCredentials",
                table: "WorkspaceLlmPolicies");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "LlmCredentials");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "LlmCredentials");
        }
    }
}
