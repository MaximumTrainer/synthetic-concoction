using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fabricate.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSnapshotPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProfileSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<long>(type: "bigint", nullable: false),
                    Tables = table.Column<string>(type: "text", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchemaSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<long>(type: "bigint", nullable: false),
                    Schema = table.Column<string>(type: "text", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSnapshots_WorkspaceId_Version",
                table: "ProfileSnapshots",
                columns: new[] { "WorkspaceId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaSnapshots_WorkspaceId_Version",
                table: "SchemaSnapshots",
                columns: new[] { "WorkspaceId", "Version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileSnapshots");

            migrationBuilder.DropTable(
                name: "SchemaSnapshots");
        }
    }
}
