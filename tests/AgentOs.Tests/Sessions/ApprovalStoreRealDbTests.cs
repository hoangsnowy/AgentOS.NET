// Approval requests (E1.5) on real Postgres: the AddApprovals migration applies, a request is stored once per (tenant,
// task, gate, change) — also against a raw twin row (unique index) — a decision is a conditional write that two
// approvers racing in separate contexts can't flip, every read is tenant-explicit, and RLS binds sessions.approvals
// (as the app role, tenant A sees only its rows and can't write B's). Needs RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN.

using System;
using System.Threading.Tasks;
using AgentOs.Domain.Governance;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using AgentOs.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class ApprovalStoreRealDbTests
{
    private static string? ServerConnection =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E_REAL"), "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("AGENTOS_PG_CONN")
            : null;

    private static ApprovalRequest Ask(string tenant, Guid board, Guid session, ApprovalGate gate, int run = 0) =>
        ApprovalRequest.New(tenant, board, session, 7, gate, run, "#7 Add dark mode", "https://github.com/o/r/pull/3", "Autopilot", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Ef_OnRealPostgres_AsksOnce_DecidesOnce_IsTenantIsolated_AndRlsBindsTheTable()
    {
        var server = ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        var database = "agentos_approvals_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        var cs = new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = false }.ConnectionString;
        await Exec(admin, $"CREATE DATABASE \"{database}\"");
        try
        {
            var options = new DbContextOptionsBuilder<SessionsDbContext>()
                .UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "sessions")).Options;
            await using (var db = new SessionsDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            var board = Guid.NewGuid();
            var session = Guid.NewGuid();
            Guid mergeId;
            await using (var db = new SessionsDbContext(options))
            {
                var store = new EfApprovalStore(db);
                var first = await store.RequestAsync(Ask("tenant-a", board, session, ApprovalGate.Merge));
                (await store.RequestAsync(Ask("tenant-a", board, session, ApprovalGate.Merge))).Id.ShouldBe(first.Id);
                await store.RequestAsync(Ask("tenant-a", board, session, ApprovalGate.Merge, run: 1));
                await store.RequestAsync(Ask("tenant-b", board, session, ApprovalGate.Merge));
                mergeId = first.Id;
            }

            // Two approvers in separate contexts: the first decision stands, the second can't flip it.
            await using (var alice = new SessionsDbContext(options))
            await using (var bob = new SessionsDbContext(options))
            {
                var aliceSaw = await new EfApprovalStore(alice).FindAsync("tenant-a", mergeId);
                var bobSaw = await new EfApprovalStore(bob).FindAsync("tenant-a", mergeId);
                aliceSaw!.Status.ShouldBe(ApprovalStatus.Pending);
                bobSaw!.Status.ShouldBe(ApprovalStatus.Pending);
                (await new EfApprovalStore(alice).DecideAsync("tenant-a", mergeId, new ApprovalDecision(true, "alice", null, DateTimeOffset.UtcNow)))!
                    .Status.ShouldBe(ApprovalStatus.Approved);
                var late = await new EfApprovalStore(bob).DecideAsync("tenant-a", mergeId, new ApprovalDecision(false, "bob", "no", DateTimeOffset.UtcNow));
                late!.Status.ShouldBe(ApprovalStatus.Approved);
                late.DecidedBy.ShouldBe("alice");
            }

            await using (var db = new SessionsDbContext(options))
            {
                var store = new EfApprovalStore(db);
                (await store.GetAsync("tenant-a", session, ApprovalGate.Merge, 0))!.DecidedBy.ShouldBe("alice");
                (await store.GetAsync("tenant-a", session, ApprovalGate.Merge, 1))!.Status.ShouldBe(ApprovalStatus.Pending);
                (await store.GetAsync("tenant-b", session, ApprovalGate.Merge, 0))!.Status.ShouldBe(ApprovalStatus.Pending);
                (await store.FindAsync("tenant-b", mergeId)).ShouldBeNull();
                (await store.DecideAsync("tenant-b", mergeId, new ApprovalDecision(false, "eve", "x", DateTimeOffset.UtcNow))).ShouldBeNull();
                (await store.ListRecentAsync("tenant-a")).Count.ShouldBe(2);
            }

            // The unique (tenant, task, gate, run) index refuses a raw twin row.
            var twin = await Should.ThrowAsync<PostgresException>(() => Exec(cs, $"""
                INSERT INTO sessions.approvals ("Id","TenantId","BoardId","SessionId","IssueNumber","Gate","Run","Status","Title","RequestedBy","RequestedAtUtc")
                VALUES (gen_random_uuid(), 'tenant-a', '{board}', '{session}', 7, 'Merge', 0, 'Approved', 't', 'x', now())
                """));
            twin.SqlState.ShouldBe("23505");

            // RLS: as the app role with tenant A set, no tenant predicate sees only A's requests; a row for B is refused.
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await Exec(conn, RowLevelSecurity.BootstrapSql(conn.PostgreSqlVersion.Major));
            await Exec(conn, $"SET ROLE {RowLevelSecurity.Role}; SELECT set_config('{RowLevelSecurity.TenantSetting}', 'tenant-a', false)");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sessions.approvals";
                ((long)(await cmd.ExecuteScalarAsync())!).ShouldBe(2L);
            }
            var refused = await Should.ThrowAsync<PostgresException>(() => Exec(conn, $"""
                INSERT INTO sessions.approvals ("Id","TenantId","BoardId","SessionId","IssueNumber","Gate","Run","Status","Title","RequestedBy","RequestedAtUtc")
                VALUES (gen_random_uuid(), 'tenant-b', '{board}', '{Guid.NewGuid()}', 7, 'Plan', 0, 'Approved', 't', 'x', now())
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

// RLS stage 2 switched ON, through the module's own context (the RLS interceptor on every connection): the Autopilot
// pass and the gate check act for NO tenant, and "no tenant" now sees nothing — so the store must bind the flow to the
// tenant it names, or every approval silently reads as "not asked" and a decision never lands. A flow bound to another
// tenant still sees nothing of the named one. Process-wide RLS state → the non-parallel collection; state restored.
[Collection(RowLevelSecurityProcessStateGroup.Name)]
public sealed class ApprovalStoreRlsEnabledRealDbTests
{
    private const string A = "approvals-on-a";
    private const string B = "approvals-on-b";

    [Fact]
    public async Task Ef_RlsEnabled_UnboundFlow_AsksFindsDecidesAndLists_UnderTheNamedTenant_NeverAnother()
    {
        var server = RowLevelSecurityStageTwoRealPostgresTests.ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        var database = "agentos_approvals_rls_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        var cs = new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = false }.ConnectionString;
        await RowLevelSecurityStageTwoRealPostgresTests.ExecAsync(admin, $"CREATE DATABASE \"{database}\"");
        var previousState = RowLevelSecurity.State;
        var previousHttp = RowLevelSecurity.HttpTenant;
        try
        {
            var options = new DbContextOptionsBuilder<SessionsDbContext>()
                .UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "sessions"))
                .AddInterceptors(RowLevelSecurityInterceptor.Instance)
                .Options;
            await using (var db = new SessionsDbContext(options))
            {
                await db.Database.MigrateAsync();   // before the switch: the login user, platform-wide
            }
            await using (var conn = new NpgsqlConnection(cs))
            {
                await conn.OpenAsync();
                await RowLevelSecurityStageTwoRealPostgresTests.ExecAsync(conn, RowLevelSecurity.BootstrapSql(conn.PostgreSqlVersion.Major));
            }
            RowLevelSecurity.HttpTenant = null;
            RowLevelSecurity.SetState(RowLevelSecurityState.Enabled);

            var session = Guid.NewGuid();
            ApprovalRequest asked;
            await using (var db = new SessionsDbContext(options))
            {
                var store = new EfApprovalStore(db);
                asked = await store.RequestAsync(ApprovalRequest.New(A, Guid.NewGuid(), session, 7, ApprovalGate.Merge, 0,
                    "#7 Add dark mode", null, "Autopilot", DateTimeOffset.UtcNow));
                (await store.RequestAsync(ApprovalRequest.New(A, Guid.NewGuid(), session, 7, ApprovalGate.Merge, 0,
                    "#7 Add dark mode", null, "Autopilot", DateTimeOffset.UtcNow))).Id.ShouldBe(asked.Id);   // asked once
                (await store.GetAsync(A, session, ApprovalGate.Merge, 0))!.Id.ShouldBe(asked.Id);
                (await store.FindAsync(A, asked.Id))!.Status.ShouldBe(ApprovalStatus.Pending);
                (await store.ListRecentAsync(A)).ShouldHaveSingleItem().Id.ShouldBe(asked.Id);
                (await store.DecideAsync(A, asked.Id, new ApprovalDecision(true, "alice", null, DateTimeOffset.UtcNow)))!
                    .Status.ShouldBe(ApprovalStatus.Approved);
                (await store.GetAsync(A, session, ApprovalGate.Merge, 0))!.Status.ShouldBe(ApprovalStatus.Approved);
            }

            // A flow bound to B that names A: RLS keeps it on B — nothing of A is read or changed.
            await using (var db = new SessionsDbContext(options))
            using (AmbientIdentity.Push(B, "eve"))
            {
                var store = new EfApprovalStore(db);
                (await store.FindAsync(A, asked.Id)).ShouldBeNull();
                (await store.ListRecentAsync(A)).ShouldBeEmpty();
                (await store.DecideAsync(A, asked.Id, new ApprovalDecision(false, "eve", "x", DateTimeOffset.UtcNow))).ShouldBeNull();
            }
            await using (var db = new SessionsDbContext(options))
            {
                (await new EfApprovalStore(db).FindAsync(A, asked.Id))!.DecidedBy.ShouldBe("alice");
            }
        }
        finally
        {
            RowLevelSecurity.SetState(previousState);
            RowLevelSecurity.HttpTenant = previousHttp;
            NpgsqlConnection.ClearAllPools();
            await RowLevelSecurityStageTwoRealPostgresTests.ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }
}
