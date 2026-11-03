using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Workspaces.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardTracker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Tracker",
                schema: "workspaces",
                table: "workspaces",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Tracker",
                schema: "workspaces",
                table: "workspaces");
        }
    }
}
