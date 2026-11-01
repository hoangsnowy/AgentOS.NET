// GAP-08 (docs/security/threat-model.md §15): a tenant export never carries secrets. The known redaction map names the
// real columns (config.app_config.EncryptedValue — the old map said "Value" and exported every ciphertext —
// sessions.runners.TokenHash, tenants.api_tokens.TokenHash), any column named like credential material is dropped too,
// and a mapped column that disappears (a rename) fails the export instead of leaking it.
// The real-Postgres test migrates the three owning modules into a throw-away database, seeds a secret in each, exports,
// and asserts none of them is in the document.
//   - Real-DB gate: RUN_AGENTOS_E2E_REAL=true + AGENTOS_PG_CONN (a server the test may CREATE/DROP DATABASE on).

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentOs.Modules.AppConfig.Persistence;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Data;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.Modules.Tenants.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class TenantExportRedactionTests
{
    [Fact]
    public void RedactedColumns_AppConfig_DropsEncryptedValue()
        => TenantDataService.RedactedColumns("config.app_config", ["TenantId", "Key", "EncryptedValue", "UpdatedAtUtc"])
            .ShouldBe(["EncryptedValue"]);

    [Theory]
    [InlineData("sessions.runners")]
    [InlineData("tenants.api_tokens")]
    public void RedactedColumns_TokenTables_DropTokenHash(string table)
        => TenantDataService.RedactedColumns(table, ["Id", "TenantId", "Prefix", "TokenHash"]).ShouldBe(["TokenHash"]);

    [Fact]
    public void RedactedColumns_UnknownTable_DropsColumnsNamedLikeSecrets()
        => TenantDataService.RedactedColumns("future.table", ["Id", "TenantId", "TotalTokensIn", "WebhookSecret", "PasswordHash", "LegacyEncryptedValue", "Name"])
            .ShouldBe(["LegacyEncryptedValue", "PasswordHash", "WebhookSecret"]);

    // The bug class itself: a map entry naming a column that isn't there must not silently export the real one.
    [Fact]
    public void RedactedColumns_MappedColumnMissing_FailsClosed()
        => Should.Throw<InvalidOperationException>(() => TenantDataService.RedactedColumns("config.app_config", ["TenantId", "Key", "Value"]))
            .Message.ShouldContain("EncryptedValue");

    // ── Real Postgres ──

    private static string? ServerConnection =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E_REAL"), "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("AGENTOS_PG_CONN")
            : null;

    [Fact]
    public async Task Export_OnRealPostgres_SeededSecretsAreAbsent_OtherDataIsPresent()
    {
        var server = ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        var database = "agentos_export_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        var cs = new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = false }.ConnectionString;
        await Exec(admin, $"CREATE DATABASE \"{database}\"");
        try
        {
            await using (var db = new AppConfigDbContext(new DbContextOptionsBuilder<AppConfigDbContext>()
                .UseNpgsql(cs, n => n.MigrationsHistoryTable("__EFMigrationsHistory", "config")).Options))
            {
                await db.Database.MigrateAsync();
            }
            await using (var db = new SessionsDbContext(new DbContextOptionsBuilder<SessionsDbContext>()
                .UseNpgsql(cs, n => n.MigrationsHistoryTable("__EFMigrationsHistory", "sessions")).Options))
            {
                await db.Database.MigrateAsync();
            }
            await using (var db = new TenantsDbContext(new DbContextOptionsBuilder<TenantsDbContext>()
                .UseNpgsql(cs, n => n.MigrationsHistoryTable("__EFMigrationsHistory", "tenants")).Options))
            {
                await db.Database.MigrateAsync();
            }

            const string tenant = "acme";
            const string configSecret = "CfDJ8-ciphertext-of-sk-ant-api03-SEEDED";
            const string runnerHash = "runner-hash-SEEDED-0123456789abcdef";
            const string apiTokenHash = "api-token-sha256-SEEDED-fedcba9876543210";
            await using (var conn = new NpgsqlConnection(cs))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO config.app_config ("TenantId","Key","EncryptedValue","UpdatedAtUtc") VALUES (@t, 'Llm:Claude:ApiKey', @c, now());
                    INSERT INTO sessions.runners ("Id","TenantId","OwnerUserId","Label","TokenHash","Status","CreatedAtUtc")
                    VALUES (gen_random_uuid(), @t, 'u1', 'laptop-marker', @r, 'Paired', now());
                    INSERT INTO tenants.api_tokens ("Id","TenantId","Name","Prefix","TokenHash","Role","CreatedBy","CreatedAtUtc","ExpiresAtUtc")
                    VALUES (gen_random_uuid(), @t, 'ci-token-marker', 'aos_abc', @a, 'member', 'u1', now(), now() + interval '30 days');
                    """;
                cmd.Parameters.AddWithValue("t", tenant);
                cmd.Parameters.AddWithValue("c", configSecret);
                cmd.Parameters.AddWithValue("r", runnerHash);
                cmd.Parameters.AddWithValue("a", apiTokenHash);
                await cmd.ExecuteNonQueryAsync();
            }

            var service = new TenantDataService(cs, Substitute.For<IKeycloakAdminClient>(), Substitute.For<IAuditLog>(),
                TimeProvider.System, NullLogger<TenantDataService>.Instance);
            using var output = new MemoryStream();
            await service.ExportAsync(tenant, output);
            var text = Encoding.UTF8.GetString(output.ToArray());

            text.ShouldNotContain(configSecret);
            text.ShouldNotContain(runnerHash);
            text.ShouldNotContain(apiTokenHash);
            text.ShouldNotContain("EncryptedValue");
            text.ShouldNotContain("TokenHash");

            using var doc = JsonDocument.Parse(text);
            var tables = doc.RootElement.GetProperty("tables");
            tables.GetProperty("config.app_config").EnumerateArray().Single().GetProperty("Key").GetString().ShouldBe("Llm:Claude:ApiKey");
            tables.GetProperty("sessions.runners").GetRawText().ShouldContain("laptop-marker");
            tables.GetProperty("tenants.api_tokens").GetRawText().ShouldContain("ci-token-marker");
        }
        finally
        {
            await Exec(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static async Task Exec(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
