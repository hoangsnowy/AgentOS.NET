using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Pipeline.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCacheTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CacheReadTokens",
                schema: "pipeline",
                table: "run_metrics",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CacheWriteTokens",
                schema: "pipeline",
                table: "run_metrics",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReasoningTokens",
                schema: "pipeline",
                table: "run_metrics",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                schema: "pipeline",
                table: "run_metrics");

            migrationBuilder.DropColumn(
                name: "CacheWriteTokens",
                schema: "pipeline",
                table: "run_metrics");

            migrationBuilder.DropColumn(
                name: "ReasoningTokens",
                schema: "pipeline",
                table: "run_metrics");
        }
    }
}
