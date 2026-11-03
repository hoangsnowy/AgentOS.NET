// REAL-DB — Postgres row-level security (ADR-0012, stage 2 — GAP-14) on the running stack's database, after the real
// hosts bootstrapped it. Proves the database itself — not the application's filters — keeps tenants apart:
//   • EVERY table in RowLevelSecurity.Tables has the agentos_tenant policy, RLS enabled AND forced (the owner is bound
//     too), with the stage-2 predicate (no "unset tenant ⇒ visible" escape);
//   • the audit trail is append-only for the app role (no UPDATE / DELETE / TRUNCATE privilege — GAP-11b);
//   • as the app role with tenant A set, a query with NO tenant predicate returns only A's rows; writing B's is refused;
//   • with no tenant set, nothing is visible (stage 1 showed everything); an explicit platform scope sees both.
// Seeds as the connection user (superuser, bypasses RLS) and removes exactly what it inserted. Every table is also
// covered behaviourally, on a throwaway database, by AgentOs.Tests' RowLevelSecurityStageTwoRealPostgresTests.
//   - Gate: RUN_AGENTOS_E2E_REAL=true and AGENTOS_PG_CONN (the stack's `agentos` database).

using System;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Persistence;
using Npgsql;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class RowLevelSecurityRealDbTests
{
    private static string? Conn =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E_REAL"), "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("AGENTOS_PG_CONN")
            : null;

    [Fact]
    public async Task Policies_ExistAreForcedAndStageTwo_OnEveryTable()
    {
        var cs = Conn ?? string.Empty;
        if (cs.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        foreach (var (schema, table) in RowLevelSecurity.Tables)
        {
            var name = $"{schema}.{table}";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT c.relrowsecurity, c.relforcerowsecurity, p.qual
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_policies p ON p.schemaname = n.nspname AND p.tablename = c.relname AND p.policyname = 'agentos_tenant'
                WHERE n.nspname = @s AND c.relname = @t
                """;
            cmd.Parameters.AddWithValue("s", schema);
            cmd.Parameters.AddWithValue("t", table);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync(), $"{name} missing");
            Assert.True(r.GetBoolean(0), $"{name}: RLS not enabled");
            Assert.True(r.GetBoolean(1), $"{name}: RLS not forced");
            Assert.False(r.IsDBNull(2), $"{name}: agentos_tenant policy missing");
            var qual = r.GetString(2);
            Assert.Contains("NULLIF", qual, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("COALESCE", qual, StringComparison.OrdinalIgnoreCase);   // the stage-1 escape
        }
    }

    [Fact]
    public async Task AppendOnlyTables_TheAppRoleCannotChangeOrRemoveRows()
    {
        var cs = Conn ?? string.Empty;
        if (cs.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        foreach (var (schema, table) in RowLevelSecurity.AppendOnlyTables)
        {
            foreach (var (privilege, granted) in new[] { ("SELECT", true), ("INSERT", true), ("UPDATE", false), ("DELETE", false), ("TRUNCATE", false) })
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT has_table_privilege(@role, @table, @privilege)";
                cmd.Parameters.AddWithValue("role", RowLevelSecurity.Role);
                cmd.Parameters.AddWithValue("table", $"{schema}.{table}");
                cmd.Parameters.AddWithValue("privilege", privilege);
                Assert.Equal(granted, (bool)(await cmd.ExecuteScalarAsync())!);
            }
        }
    }

    [Fact]
    public async Task AppRole_SeesOnlyItsTenant_NoTenantSeesNothing_AndCannotWriteAnother()
    {
        var cs = Conn ?? string.Empty;
        if (cs.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }
        var id = Guid.NewGuid().ToString("N")[..8];
        var (a, b, marker) = ($"rls-a-{id}", $"rls-b-{id}", $"rls-{id}");
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        try
        {
            await Exec(conn, $"""
                INSERT INTO workspaces.workspaces ("Id","TenantId","Name","Kind","CredentialRef","CreatedAtUtc","Status","ProjectOwner","ProjectScope")
                VALUES (gen_random_uuid(), '{a}', '{marker}', 0, 'none', now(), 'Connected', '', ''),
                       (gen_random_uuid(), '{b}', '{marker}', 0, 'none', now(), 'Connected', '', '')
                """);

            // As the app role, tenant A, a query that FORGOT its tenant predicate:
            await Exec(conn, $"SET ROLE {RowLevelSecurity.Role}; SELECT set_config('{RowLevelSecurity.TenantSetting}', '{a}', false)");
            Assert.Equal(1L, await Count(conn, marker));
            Assert.Equal(0L, await Count(conn, marker, tenant: b));   // asking for B explicitly still sees nothing

            var refused = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"""
                INSERT INTO workspaces.workspaces ("Id","TenantId","Name","Kind","CredentialRef","CreatedAtUtc","Status","ProjectOwner","ProjectScope")
                VALUES (gen_random_uuid(), '{b}', '{marker}-x', 0, 'none', now(), 'Connected', '', '')
                """));
            Assert.Equal("42501", refused.SqlState);   // new row violates row-level security policy

            // Stage 2: no tenant known → nothing (it was "everything" in stage 1).
            await Exec(conn, $"SELECT set_config('{RowLevelSecurity.TenantSetting}', '', false)");
            Assert.Equal(0L, await Count(conn, marker));

            // An explicit platform scope (the scheduler, the autopilot scan) sees every tenant.
            await Exec(conn, $"SELECT set_config('{RowLevelSecurity.PlatformSetting}', 'on', false)");
            Assert.Equal(2L, await Count(conn, marker));
        }
        finally
        {
            await Exec(conn, "RESET ROLE");
            await Exec(conn, $"DELETE FROM workspaces.workspaces WHERE \"Name\" LIKE '{marker}%'");
        }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Count(NpgsqlConnection conn, string marker, string? tenant = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM workspaces.workspaces WHERE \"Name\" = @m" + (tenant is null ? "" : " AND \"TenantId\" = @t");
        cmd.Parameters.AddWithValue("m", marker);
        if (tenant is not null) { cmd.Parameters.AddWithValue("t", tenant); }
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
