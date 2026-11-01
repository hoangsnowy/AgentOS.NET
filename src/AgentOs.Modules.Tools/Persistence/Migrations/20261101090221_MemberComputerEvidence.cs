using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Tools.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemberComputerEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentRole",
                schema: "tools",
                table: "tool_invocations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Denied",
                schema: "tools",
                table: "tool_invocations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                schema: "tools",
                table: "tool_invocations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicySource",
                schema: "tools",
                table: "tool_invocations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunnerId",
                schema: "tools",
                table: "tool_invocations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tool_invocations_TenantId_Origin_StartedUtc",
                schema: "tools",
                table: "tool_invocations",
                columns: new[] { "TenantId", "Origin", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tool_invocations_TenantId_Origin_StartedUtc",
                schema: "tools",
                table: "tool_invocations");

            migrationBuilder.DropColumn(
                name: "AgentRole",
                schema: "tools",
                table: "tool_invocations");

            migrationBuilder.DropColumn(
                name: "Denied",
                schema: "tools",
                table: "tool_invocations");

            migrationBuilder.DropColumn(
                name: "Origin",
                schema: "tools",
                table: "tool_invocations");

            migrationBuilder.DropColumn(
                name: "PolicySource",
                schema: "tools",
                table: "tool_invocations");

            migrationBuilder.DropColumn(
                name: "RunnerId",
                schema: "tools",
                table: "tool_invocations");
        }
    }
}
