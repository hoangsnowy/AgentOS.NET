// REAL-POSTGRES — RLS stage 2 (ADR-0012, GAP-14) on EVERY table in RowLevelSecurity.Tables, plus GAP-11b's append-only
// audit trail. A throwaway database gets every module's migrations and the real bootstrap script; one row per tenant is
// seeded in every table (values generated from the catalog, foreign-key triggers off). Then, as the app role and with
// NO tenant predicate in any query:
//   • tenant A sees exactly A's rows (plus the shared "__platform" rows of config.app_config) and cannot write B's;
//   • no tenant sees nothing and can write nothing (stage 1 showed it everything);
//   • an explicit platform scope sees every row;
//   • tenants.audit_events: INSERT works, UPDATE / DELETE / TRUNCATE are refused (permission denied);
//   • config.app_config: a tenant reads the platform rows but can neither change, delete nor add one;
//   • a table OWNER that is not a superuser — how Azure connects — is bound too (FORCE): no tenant ⇒ nothing.
// And the catalog check: every table with a "TenantId" column is in RowLevelSecurity.Tables or ExemptTables.
//   Gate: RUN_AGENTOS_E2E_REAL=true and AGENTOS_PG_CONN (a server the test may CREATE / DROP DATABASE and ROLE on — the
//   dev/lab stack's superuser, or a throwaway container). Zero residue: the database and the role are dropped.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Modules.AppConfig.Persistence;
using AgentOs.Modules.Memory.Persistence;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Tenants.Persistence;
using AgentOs.Modules.Tools.Persistence;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Persistence;

public sealed class RowLevelSecurityStageTwoRealPostgresTests
{
    private const string A = "rls-tenant-a";
    private const string B = "rls-tenant-b";
    private const string AppConfig = "config.app_config";
    private const string AuditEvents = "tenants.audit_events";

    internal static string? ServerConnection =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E_REAL"), "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("AGENTOS_PG_CONN")
            : null;

    [Fact]
    public async Task StageTwo_EveryTenantTable_NoTenantSeesNothing_TenantSeesItsOwn_PlatformSeesAll()
    {
        var server = ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        var database = "agentos_rls2_" + Guid.NewGuid().ToString("N")[..12];
        var owner = "agentos_rls2_owner_" + Guid.NewGuid().ToString("N")[..8];
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        var cs = new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = false }.ConnectionString;
        await ExecAsync(admin, $"CREATE DATABASE \"{database}\"");
        try
        {
            await MigrateEveryModuleAsync(cs);
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await ExecAsync(conn, RowLevelSecurity.BootstrapSql(conn.PostgreSqlVersion.Major));

            // 1. Every tenant table is either under RLS or exempt on purpose — and every listed table exists.
            var tenantTables = await TenantTablesAsync(conn);
            var covered = RowLevelSecurity.Tables.Select(t => $"{t.Schema}.{t.Table}").ToHashSet(StringComparer.Ordinal);
            tenantTables.Except(covered).Except(RowLevelSecurity.ExemptTables.Keys)
                .ShouldBeEmpty("Tables with a TenantId column that are neither in RowLevelSecurity.Tables nor ExemptTables");
            covered.Except(tenantTables).ShouldBeEmpty("RowLevelSecurity.Tables names a table no migration creates");

            // 2. Enabled, forced, and the stage-2 policy (no stage-1 "unset tenant ⇒ visible" escape).
            foreach (var table in covered)
            {
                var (enabled, forced, qual, check) = await PolicyAsync(conn, table);
                enabled.ShouldBeTrue($"{table}: RLS not enabled");
                forced.ShouldBeTrue($"{table}: RLS not forced");
                qual.ShouldNotBeNull($"{table}: agentos_tenant policy missing");
                foreach (var expr in new[] { qual, check! })
                {
                    expr.ShouldContain("NULLIF", Case.Insensitive, $"{table}: {expr}");
                    expr.ShouldContain(RowLevelSecurity.PlatformSetting, Case.Sensitive, $"{table}: {expr}");
                    expr.ShouldNotContain("COALESCE", Case.Insensitive, $"{table}: {expr}");
                }
            }

            // 3. Seed one row for A and one for B in every table (+ a platform settings row), as the superuser.
            var columns = await RequiredColumnsAsync(conn);
            var seed = 0;
            await ExecAsync(conn, "SET session_replication_role = replica");   // FK triggers off: no parent rows needed
            foreach (var table in covered)
            {
                await InsertAsync(conn, table, columns[table], A, ++seed);
                await InsertAsync(conn, table, columns[table], B, ++seed);
            }
            await InsertAsync(conn, AppConfig, columns[AppConfig], RowLevelSecurity.PlatformSettingsTenant, ++seed);
            await ExecAsync(conn, "RESET session_replication_role");
            var baseline = new Dictionary<string, (long A, long Shared, long All)>(StringComparer.Ordinal);
            foreach (var table in covered)
            {
                baseline[table] = (await CountAsync(conn, table, A), table == AppConfig ? await CountAsync(conn, table, RowLevelSecurity.PlatformSettingsTenant) : 0,
                    await CountAsync(conn, table));
            }

            // 4. As the app role — every query below has NO tenant predicate.
            await ExecAsync(conn, $"SET ROLE {RowLevelSecurity.Role}");
            foreach (var table in covered)
            {
                var expected = baseline[table];
                await SessionAsync(conn, tenant: A, platform: false);
                (await CountAsync(conn, table)).ShouldBe(expected.A + expected.Shared, $"{table}: tenant A");
                (await RefusedAsync(() => InsertAsync(conn, table, columns[table], B, ++seed))).ShouldContain("row-level security", Case.Insensitive, table);

                await SessionAsync(conn, tenant: null, platform: false);
                (await CountAsync(conn, table)).ShouldBe(expected.Shared, $"{table}: no tenant must see nothing");
                (await RefusedAsync(() => InsertAsync(conn, table, columns[table], A, ++seed))).ShouldContain("row-level security", Case.Insensitive, table);

                await SessionAsync(conn, tenant: null, platform: true);
                (await CountAsync(conn, table)).ShouldBe(expected.All, $"{table}: platform scope");
            }

            // 5. GAP-11b: the audit trail is append-only for the app role.
            await SessionAsync(conn, tenant: A, platform: false);
            await InsertAsync(conn, AuditEvents, columns[AuditEvents], A, ++seed);
            (await RefusedAsync(() => ExecAsync(conn, "UPDATE tenants.audit_events SET \"Action\" = 'forged'"))).ShouldContain("permission denied");
            (await RefusedAsync(() => ExecAsync(conn, "DELETE FROM tenants.audit_events"))).ShouldContain("permission denied");
            (await RefusedAsync(() => ExecAsync(conn, "TRUNCATE tenants.audit_events"))).ShouldContain("permission denied");
            await SessionAsync(conn, tenant: null, platform: true);   // not even a platform operation may rewrite it
            (await RefusedAsync(() => ExecAsync(conn, "DELETE FROM tenants.audit_events"))).ShouldContain("permission denied");

            // 6. Platform settings: readable by every tenant, changed only by an explicit platform operation.
            await SessionAsync(conn, tenant: A, platform: false);
            (await ExecCountAsync(conn, "UPDATE config.app_config SET \"EncryptedValue\" = 'x' WHERE \"TenantId\" = '__platform'")).ShouldBe(0);
            (await ExecCountAsync(conn, "DELETE FROM config.app_config WHERE \"TenantId\" = '__platform'")).ShouldBe(0);
            (await RefusedAsync(() => InsertAsync(conn, AppConfig, columns[AppConfig], RowLevelSecurity.PlatformSettingsTenant, ++seed)))
                .ShouldContain("row-level security", Case.Insensitive);
            await SessionAsync(conn, tenant: null, platform: true);
            await InsertAsync(conn, AppConfig, columns[AppConfig], RowLevelSecurity.PlatformSettingsTenant, ++seed);
            await ExecAsync(conn, "RESET ROLE");

            // 7. A table owner that is not a superuser (Azure Flexible Server) is bound by FORCE as well.
            await ExecAsync(conn, $"CREATE ROLE \"{owner}\" NOLOGIN; GRANT USAGE ON SCHEMA workspaces TO \"{owner}\"; ALTER TABLE workspaces.workspaces OWNER TO \"{owner}\"");
            var workspaces = baseline["workspaces.workspaces"];
            await ExecAsync(conn, $"SET ROLE \"{owner}\"");
            await SessionAsync(conn, tenant: null, platform: false);
            (await CountAsync(conn, "workspaces.workspaces")).ShouldBe(0L, "the owner with no tenant must see nothing");
            await SessionAsync(conn, tenant: A, platform: false);
            (await CountAsync(conn, "workspaces.workspaces")).ShouldBe(workspaces.A);
            await SessionAsync(conn, tenant: null, platform: true);
            (await CountAsync(conn, "workspaces.workspaces")).ShouldBe(workspaces.All);
            await ExecAsync(conn, "RESET ROLE");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await ExecAsync(admin, $"DROP ROLE IF EXISTS \"{owner}\"");
        }
    }

    // ── helpers (shared with the other real-Postgres RLS tests in this folder) ──

    internal static async Task MigrateEveryModuleAsync(string cs)
    {
        var tenant = new FixedTenant("migrations");
        await using (var db = new PipelineDbContext(Options<PipelineDbContext>(cs, "pipeline"), tenant)) { await db.Database.MigrateAsync(); }
        await using (var db = new SessionsDbContext(Options<SessionsDbContext>(cs, "sessions"))) { await db.Database.MigrateAsync(); }
        await using (var db = new ToolsDbContext(Options<ToolsDbContext>(cs, "tools"))) { await db.Database.MigrateAsync(); }
        await using (var db = new WorkspacesDbContext(Options<WorkspacesDbContext>(cs, "workspaces"))) { await db.Database.MigrateAsync(); }
        await using (var db = new MemoryDbContext(Options<MemoryDbContext>(cs, "memory"))) { await db.Database.MigrateAsync(); }
        await using (var db = new AppConfigDbContext(Options<AppConfigDbContext>(cs, "config"))) { await db.Database.MigrateAsync(); }
        await using (var db = new TenantsDbContext(Options<TenantsDbContext>(cs, "tenants"))) { await db.Database.MigrateAsync(); }
    }

    internal static DbContextOptions<T> Options<T>(string cs, string schema) where T : DbContext
        => new DbContextOptionsBuilder<T>().UseNpgsql(cs, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options;

    private static async Task<HashSet<string>> TenantTablesAsync(NpgsqlConnection conn)
    {
        const string sql = """
            SELECT n.nspname || '.' || c.relname
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE a.attname = 'TenantId' AND c.relkind = 'r' AND NOT a.attisdropped
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            """;
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { set.Add(reader.GetString(0)); }
        return set;
    }

    private static async Task<(bool Enabled, bool Forced, string? Qual, string? Check)> PolicyAsync(NpgsqlConnection conn, string table)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT c.relrowsecurity, c.relforcerowsecurity, p.qual, p.with_check
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_policies p ON p.schemaname = n.nspname AND p.tablename = c.relname AND p.policyname = 'agentos_tenant'
            WHERE n.nspname || '.' || c.relname = @t
            """, conn);
        cmd.Parameters.AddWithValue("t", table);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue($"{table} missing");
        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    internal sealed record Column(string Name, string Type, int? MaxLength);

    // The columns an INSERT must name: NOT NULL, no default, not generated / identity.
    internal static async Task<Dictionary<string, List<Column>>> RequiredColumnsAsync(NpgsqlConnection conn)
    {
        const string sql = """
            SELECT table_schema || '.' || table_name, column_name, udt_name, character_maximum_length
            FROM information_schema.columns
            WHERE is_nullable = 'NO' AND column_default IS NULL AND is_generated = 'NEVER' AND is_identity = 'NO'
              AND table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_name, ordinal_position
            """;
        var byTable = new Dictionary<string, List<Column>>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            if (!byTable.TryGetValue(table, out var list)) { byTable[table] = list = []; }
            list.Add(new Column(reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }
        return byTable;
    }

    // One row with generated values: the tenant in "TenantId", a unique value per seed in every other required column.
    internal static async Task InsertAsync(NpgsqlConnection conn, string table, IReadOnlyList<Column> columns, string tenant, int seed)
    {
        var names = string.Join(", ", columns.Select(c => $"\"{c.Name}\""));
        var values = string.Join(", ", columns.Select(c => c.Name == "TenantId" ? "@tenant" : Literal(c, seed)));
        var parts = table.Split('.');
        await using var cmd = new NpgsqlCommand($"INSERT INTO \"{parts[0]}\".\"{parts[1]}\" ({names}) VALUES ({values})", conn);
        cmd.Parameters.AddWithValue("tenant", tenant);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Literal(Column c, int seed)
    {
        var text = "r" + seed.ToString(CultureInfo.InvariantCulture);
        return c.Type switch
        {
            "varchar" or "text" or "bpchar" or "citext" => $"'{(c.MaxLength is { } max && text.Length > max ? text[..max] : text)}'",
            "uuid" => "gen_random_uuid()",
            "int2" or "int4" or "int8" or "numeric" or "float4" or "float8" => seed.ToString(CultureInfo.InvariantCulture),
            "bool" => "false",
            "timestamptz" or "timestamp" => "now()",
            "date" => "current_date",
            "bytea" => "'\\x00'::bytea",
            "jsonb" or "json" => "'{}'::" + c.Type,
            "interval" => "interval '0'",
            "tsvector" => "''::tsvector",
            _ when c.Type.StartsWith('_') => $"'{{}}'::{c.Type[1..]}[]",
            _ => throw new InvalidOperationException($"No seed value for {c.Name} ({c.Type}) — extend Literal()."),
        };
    }

    internal static async Task SessionAsync(NpgsqlConnection conn, string? tenant, bool platform)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT set_config('{RowLevelSecurity.TenantSetting}', @t, false), set_config('{RowLevelSecurity.PlatformSetting}', @p, false)", conn);
        cmd.Parameters.AddWithValue("t", tenant ?? string.Empty);
        cmd.Parameters.AddWithValue("p", platform ? "on" : string.Empty);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<long> CountAsync(NpgsqlConnection conn, string table, string? tenant = null)
    {
        var parts = table.Split('.');
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{parts[0]}\".\"{parts[1]}\"" + (tenant is null ? string.Empty : " WHERE \"TenantId\" = @t"), conn);
        if (tenant is not null) { cmd.Parameters.AddWithValue("t", tenant); }
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // The statement must fail with 42501 (an RLS violation or a missing privilege); returns the message.
    internal static async Task<string> RefusedAsync(Func<Task> statement)
    {
        var error = await Should.ThrowAsync<PostgresException>(statement);
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, error.MessageText);
        return error.MessageText;
    }

    internal static async Task ExecAsync(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await ExecAsync(conn, sql);
    }

    internal static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecCountAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal sealed class FixedTenant(string id) : ITenantContext
    {
        public string TenantId { get; } = id;
        public string? UserId => null;
        public string? UserName => null;
        public IReadOnlyList<string> Roles { get; } = [];
        public bool IsAuthenticated => false;
        public bool IsAdmin => false;
    }
}
