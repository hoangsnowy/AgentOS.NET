// REAL-DB — Postgres row-level security (ADR-0012, stage 1) on the running stack's database. Proves the
// database itself — not the application's filters — keeps tenants apart:
//   • every stage-1 table has the agentos_tenant policy, RLS enabled AND forced (the owner is bound too);
//   • as the app role with tenant A set, a query with NO tenant predicate returns only A's rows;
//   • inserting a row for tenant B is refused (WITH CHECK);
//   • with no tenant set, stage 1 stays permissive (pairing / background paths keep working).
// Seeds as the connection user (superuser, bypasses RLS) and removes exactly what it inserted.
//   - Gate: RUN_AGENTOS_E2E_REAL=true and AGENTOS_PG_CONN (the stack's `agentos` database).

using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class RowLevelSecurityRealDbTests
{
    private static readonly string[] Tables =
    [
        "pipeline.pipeline_runs", "pipeline.run_metrics", "pipeline.orchestrations",
        "sessions.sessions", "sessions.session_repos", "sessions.runners", "sessions.ticket_files",
        "tools.tool_invocations", "workspaces.workspaces", "workspaces.workspace_repos", "workspaces.ticket_flows",
    ];

    private static string? Conn =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E_REAL"), "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("AGENTOS_PG_CONN")
            : null;

    [Fact]
    public async Task Policies_ExistAndAreForced_OnEveryStageOneTable()
    {
        var cs = Conn ?? string.Empty;
        if (cs.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        foreach (var table in Tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT c.relrowsecurity, c.relforcerowsecurity,
                       EXISTS (SELECT 1 FROM pg_policies p WHERE p.schemaname = n.nspname AND p.tablename = c.relname AND p.policyname = 'agentos_tenant')
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname || '.' || c.relname = @t
                """;
            cmd.Parameters.AddWithValue("t", table);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync(), $"{table} missing");
            Assert.True(r.GetBoolean(0), $"{table}: RLS not enabled");
            Assert.True(r.GetBoolean(1), $"{table}: RLS not forced");
            Assert.True(r.GetBoolean(2), $"{table}: agentos_tenant policy missing");
        }
    }

    [Fact]
    public async Task AppRole_SeesOnlyItsTenant_AndCannotWriteAnother()
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
            await Exec(conn, $"SET ROLE agentos_rls; SELECT set_config('app.tenant_id', '{a}', false)");
            Assert.Equal(1L, await Count(conn, marker));
            Assert.Equal(0L, await Count(conn, marker, tenant: b));   // asking for B explicitly still sees nothing

            var refused = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, $"""
                INSERT INTO workspaces.workspaces ("Id","TenantId","Name","Kind","CredentialRef","CreatedAtUtc","Status","ProjectOwner","ProjectScope")
                VALUES (gen_random_uuid(), '{b}', '{marker}-x', 0, 'none', now(), 'Connected', '', '')
                """));
            Assert.Equal("42501", refused.SqlState);   // new row violates row-level security policy

            // Stage 1: no tenant known → permissive (pairing, background drains).
            await Exec(conn, "SELECT set_config('app.tenant_id', '', false)");
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
