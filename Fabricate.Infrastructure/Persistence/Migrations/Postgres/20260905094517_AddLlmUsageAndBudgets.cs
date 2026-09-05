using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fabricate.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddLlmUsageAndBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DailyTokenBudget",
                table: "WorkspaceLlmPolicies",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MonthlyTokenBudget",
                table: "WorkspaceLlmPolicies",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LlmUsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmUsageRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LlmUsageRecords_WorkspaceId_OccurredAt",
                table: "LlmUsageRecords",
                columns: new[] { "WorkspaceId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LlmUsageRecords");

            migrationBuilder.DropColumn(
                name: "DailyTokenBudget",
                table: "WorkspaceLlmPolicies");

            migrationBuilder.DropColumn(
                name: "MonthlyTokenBudget",
                table: "WorkspaceLlmPolicies");
        }
    }
}
