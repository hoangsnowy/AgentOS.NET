using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Workspaces.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutopilotEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "autopilot_events",
                schema: "workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_autopilot_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_autopilot_events_TenantId_BoardId_AtUtc",
                schema: "workspaces",
                table: "autopilot_events",
                columns: new[] { "TenantId", "BoardId", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "autopilot_events",
                schema: "workspaces");
        }
    }
}
