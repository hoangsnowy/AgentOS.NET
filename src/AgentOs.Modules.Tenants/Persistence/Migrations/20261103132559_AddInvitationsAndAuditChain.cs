using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Tenants.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationsAndAuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Hash",
                schema: "tenants",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrevHash",
                schema: "tenants",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                schema: "tenants",
                table: "audit_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    EmailNormalized = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_TenantId_Sequence",
                schema: "tenants",
                table: "audit_events",
                columns: new[] { "TenantId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invitations_TenantId_CreatedAtUtc",
                schema: "tenants",
                table: "invitations",
                columns: new[] { "TenantId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invitations",
                schema: "tenants");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_TenantId_Sequence",
                schema: "tenants",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "Hash",
                schema: "tenants",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "PrevHash",
                schema: "tenants",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "Sequence",
                schema: "tenants",
                table: "audit_events");
        }
    }
}
