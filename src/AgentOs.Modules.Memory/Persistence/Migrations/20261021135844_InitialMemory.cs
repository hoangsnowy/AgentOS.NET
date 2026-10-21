using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace AgentOs.Modules.Memory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "memory");

            migrationBuilder.CreateTable(
                name: "edges",
                schema: "memory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FromId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_edges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "nodes",
                schema: "memory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RepoKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Body = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    Pinned = table.Column<bool>(type: "boolean", nullable: false),
                    SourceRef = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetractedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UseCount = table.Column<int>(type: "integer", nullable: false),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "simple")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "Title", "Body" })
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_edges_TenantId_FromId_Rel",
                schema: "memory",
                table: "edges",
                columns: new[] { "TenantId", "FromId", "Rel" });

            migrationBuilder.CreateIndex(
                name: "IX_edges_TenantId_ToId_Rel",
                schema: "memory",
                table: "edges",
                columns: new[] { "TenantId", "ToId", "Rel" });

            migrationBuilder.CreateIndex(
                name: "IX_nodes_SearchVector",
                schema: "memory",
                table: "nodes",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_nodes_TenantId_Key",
                schema: "memory",
                table: "nodes",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nodes_TenantId_RepoKey_Kind",
                schema: "memory",
                table: "nodes",
                columns: new[] { "TenantId", "RepoKey", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edges",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "nodes",
                schema: "memory");
        }
    }
}
