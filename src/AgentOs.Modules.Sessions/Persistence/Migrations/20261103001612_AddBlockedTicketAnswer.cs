using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Sessions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBlockedTicketAnswer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BlockedAtUtc",
                schema: "sessions",
                table: "sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TicketAnswerAtUtc",
                schema: "sessions",
                table: "sessions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockedAtUtc",
                schema: "sessions",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "TicketAnswerAtUtc",
                schema: "sessions",
                table: "sessions");
        }
    }
}
