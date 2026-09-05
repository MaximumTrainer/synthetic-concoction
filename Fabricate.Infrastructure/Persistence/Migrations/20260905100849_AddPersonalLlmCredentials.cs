using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fabricate.Infrastructure.Persistence.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "LlmCredentials",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "LlmCredentials",
                type: "TEXT",
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
