// REAL-POSTGRES — tool calls made on a member's computer, on a real database: the MemberComputerEvidence migration adds
// the columns + the (TenantId, Origin, StartedUtc) index, the EF log round-trips them, the origin/session filter and the
// per-role grouped query (COUNT DISTINCT run) translate and stay inside the tenant, and the existing RLS policy on
// tools.tool_invocations binds the new rows: as the app role with tenant A set, a query with NO tenant predicate sees
// only A's rows and writing a row for B is refused.
// It creates a throwaway database on the given server and drops it afterwards — zero residue on a shared stack.
//   Gate: RUN_AGENTOS_E2E_REAL=true and AGENTOS_PG_CONN (a connection string to any database on the server, as a user
//   that may CREATE DATABASE — the dev/lab stack's superuser).

using System;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;
using AgentOs.Modules.Tools.Evidence;
using AgentOs.Modules.Tools.Persistence;
using AgentOs.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tools;

public sealed class MemberComputerEvidenceRealPostgresTests
{
    private static string? ServerConnection =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E_REAL"), "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("AGENTOS_PG_CONN")
            : null;

    private static ToolInvocationEvidence Row(string id, string tenant, string run, string session, string role, bool denied) =>
        new(id, "Bash", tenant, run, "{\"summary\":\"git push origin x\"}", denied ? "Refused" : "Ran", denied,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(1), session)
        {
            Origin = ToolEvidenceOrigins.MemberComputer,
            AgentRole = role,
            RunnerId = Guid.NewGuid().ToString("D"),
            PolicySource = ToolEvidencePolicies.CliScopedPermissions,
            Denied = denied,
        };

    [Fact]
    public async Task EvidenceLog_OnRealPostgres_FiltersGroupsAndIsTenantIsolated()
    {
        var server = ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        var database = "agentos_toolev_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        var cs = new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = false }.ConnectionString;
        await Exec(admin, $"CREATE DATABASE \"{database}\"");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ToolsDbContext>(o => o.UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "tools")));
            await using var sp = services.BuildServiceProvider();
            await using (var scope = sp.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ToolsDbContext>().Database.MigrateAsync();
            }
            var log = new EfToolInvocationLog(sp, sp.GetRequiredService<ILogger<EfToolInvocationLog>>());

            await log.AppendAsync(Row("a1", "tenant-a", "req-1", "sess-1", "Coding", denied: false));
            await log.AppendAsync(Row("a2", "tenant-a", "req-1", "sess-1", "Coding", denied: true));
            await log.AppendAsync(Row("a3", "tenant-a", "req-2", "sess-2", "Testing", denied: false));
            await log.AppendAsync(new ToolInvocationEvidence("s1", "build_verifier", "tenant-a", "run", "{}", "ok", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await log.AppendAsync(Row("b1", "tenant-b", "req-9", "sess-1", "Coding", denied: false));

            var oneTask = await log.ListRecentAsync("tenant-a", new ToolEvidenceFilter(ToolEvidenceOrigins.MemberComputer, "sess-1"));
            oneTask.Select(r => r.CallId).ShouldBe(["a1", "a2"], ignoreOrder: true);
            oneTask.Single(r => r.CallId == "a2").Denied.ShouldBeTrue();
            oneTask.ShouldAllBe(r => r.PolicySource == ToolEvidencePolicies.CliScopedPermissions && r.AgentRole == "Coding");

            (await log.SummarizeMemberComputerAsync("tenant-a", DateTimeOffset.UtcNow.AddDays(-1)))
                .ShouldBe([new ToolCallsByRole("Coding", 1, 2, 1), new ToolCallsByRole("Testing", 1, 1, 0)]);
            (await log.SummarizeMemberComputerAsync("tenant-b", null)).ShouldHaveSingleItem().ToolCalls.ShouldBe(1);

            // RLS binds the new rows: no tenant predicate, app role, tenant A → only A's five-minus-B rows; B's insert refused.
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await Exec(conn, RowLevelSecurity.BootstrapSql(conn.PostgreSqlVersion.Major));
            await Exec(conn, $"SET ROLE {RowLevelSecurity.Role}; SELECT set_config('{RowLevelSecurity.TenantSetting}', 'tenant-a', false)");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM tools.tool_invocations WHERE \"Origin\" = 'member-computer'";
                ((long)(await cmd.ExecuteScalarAsync())!).ShouldBe(3L);
            }
            var refused = await Should.ThrowAsync<PostgresException>(() => Exec(conn, """
                INSERT INTO tools.tool_invocations ("Id","CallId","ToolName","TenantId","Input","Output","IsError","StartedUtc","FinishedUtc","Origin","Denied")
                VALUES (gen_random_uuid(), 'x', 'Bash', 'tenant-b', '{}', 'x', false, now(), now(), 'member-computer', false)
                """));
            refused.SqlState.ShouldBe("42501");
            await Exec(conn, "RESET ROLE");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Exec(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static async Task Exec(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await Exec(conn, sql);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
