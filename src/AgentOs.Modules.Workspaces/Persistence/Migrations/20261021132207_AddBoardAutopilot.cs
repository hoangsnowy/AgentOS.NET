using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Workspaces.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardAutopilot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Autopilot",
                schema: "workspaces",
                table: "workspaces",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.AddColumn<string>(
                name: "AutopilotUserId",
                schema: "workspaces",
                table: "workspaces",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_Autopilot",
                schema: "workspaces",
                table: "workspaces",
                column: "Autopilot");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workspaces_Autopilot",
                schema: "workspaces",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "Autopilot",
                schema: "workspaces",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "AutopilotUserId",
                schema: "workspaces",
                table: "workspaces");
        }
    }
}
