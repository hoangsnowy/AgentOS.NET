using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Pipeline.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scheduled_runs",
                schema: "pipeline",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Story = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Cron = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RunAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_runs_Enabled_NextRunAtUtc",
                schema: "pipeline",
                table: "scheduled_runs",
                columns: new[] { "Enabled", "NextRunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_runs_TenantId",
                schema: "pipeline",
                table: "scheduled_runs",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_runs",
                schema: "pipeline");
        }
    }
}
