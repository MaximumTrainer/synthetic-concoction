using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fabricate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiContracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiContracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GeneratedApiEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ArtifactRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BoundTable = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ResponseKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseSchemaJson = table.Column<string>(type: "TEXT", nullable: true),
                    Diagnostics = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedApiEndpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiContracts_WorkspaceId",
                table: "ApiContracts",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedApiEndpoints_WorkspaceId",
                table: "GeneratedApiEndpoints",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiContracts");

            migrationBuilder.DropTable(
                name: "GeneratedApiEndpoints");
        }
    }
}
