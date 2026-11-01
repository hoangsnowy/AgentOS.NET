using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Sessions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDesignReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "design_reviews",
                schema: "sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueNumber = table.Column<int>(type: "integer", nullable: false),
                    Run = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    FindingsJson = table.Column<string>(type: "text", nullable: false),
                    ShotsJson = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Retries = table.Column<int>(type: "integer", nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_design_reviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_design_reviews_TenantId_SessionId_Run",
                schema: "sessions",
                table: "design_reviews",
                columns: new[] { "TenantId", "SessionId", "Run" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_design_reviews_TenantId_UpdatedAtUtc",
                schema: "sessions",
                table: "design_reviews",
                columns: new[] { "TenantId", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "design_reviews",
                schema: "sessions");
        }
    }
}
