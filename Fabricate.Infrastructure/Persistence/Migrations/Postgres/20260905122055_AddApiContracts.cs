using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fabricate.Infrastructure.Persistence.Migrations.Postgres
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DocumentJson = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiContracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GeneratedApiEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OperationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ArtifactRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    BoundTable = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ResponseKind = table.Column<int>(type: "integer", nullable: false),
                    ResponseSchemaJson = table.Column<string>(type: "text", nullable: true),
                    Diagnostics = table.Column<string>(type: "text", nullable: true)
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
