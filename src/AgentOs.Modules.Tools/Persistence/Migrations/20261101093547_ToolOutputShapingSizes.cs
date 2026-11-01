using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Tools.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ToolOutputShapingSizes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OutputChars",
                schema: "tools",
                table: "tool_invocations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShapedOutputChars",
                schema: "tools",
                table: "tool_invocations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutputChars",
                schema: "tools",
                table: "tool_invocations");

            migrationBuilder.DropColumn(
                name: "ShapedOutputChars",
                schema: "tools",
                table: "tool_invocations");
        }
    }
}
