using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fabricate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptDataBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ComplianceProfile",
                table: "Workspaces",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AllowSampledDataInPrompts",
                table: "WorkspaceLlmPolicies",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComplianceProfile",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AllowSampledDataInPrompts",
                table: "WorkspaceLlmPolicies");
        }
    }
}
