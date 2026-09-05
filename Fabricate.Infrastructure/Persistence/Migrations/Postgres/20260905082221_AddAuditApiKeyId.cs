using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fabricate.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAuditApiKeyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ApiKeyId",
                table: "AuditEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_AccountId_ApiKeyId",
                table: "AuditEvents",
                columns: new[] { "AccountId", "ApiKeyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_AccountId_ApiKeyId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "AuditEvents");
        }
    }
}
