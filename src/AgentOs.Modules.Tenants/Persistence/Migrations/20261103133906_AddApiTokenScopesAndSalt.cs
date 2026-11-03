using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentOs.Modules.Tenants.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiTokenScopesAndSalt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "tenants",
                table: "api_tokens",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "service");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                schema: "tenants",
                table: "api_tokens",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Salt",
                schema: "tenants",
                table: "api_tokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Scopes",
                schema: "tenants",
                table: "api_tokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_api_tokens_TenantId_OwnerUserId",
                schema: "tenants",
                table: "api_tokens",
                columns: new[] { "TenantId", "OwnerUserId" });

            // Tokens created before scopes held only a role: member could run, admin could also administer.
            migrationBuilder.Sql("""UPDATE tenants.api_tokens SET "Scopes" = CASE WHEN "Role" = 'admin' THEN 'read run admin' ELSE 'read run' END WHERE "Scopes" = '';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_api_tokens_TenantId_OwnerUserId",
                schema: "tenants",
                table: "api_tokens");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "tenants",
                table: "api_tokens");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                schema: "tenants",
                table: "api_tokens");

            migrationBuilder.DropColumn(
                name: "Salt",
                schema: "tenants",
                table: "api_tokens");

            migrationBuilder.DropColumn(
                name: "Scopes",
                schema: "tenants",
                table: "api_tokens");
        }
    }
}
