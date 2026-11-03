// REAL-POSTGRES — GAP-11 / 11b through the real stores, as the restricted app role under RLS stage 2 (every context
// here binds its connections exactly like an RLS-enabled host does: SET ROLE agentos_rls + the flow's scope):
//   • the audit chain: sequential AND concurrent writers for one tenant produce one gap-free chain that verifies intact;
//     "_platform" rows chain separately; a flow bound to tenant B reads none of A's events; a row changed or deleted
//     behind the app's back (by the database owner) is reported at its position, and so is a row the app role inserts
//     with no chain link after the chain started;
//   • invitations: the row is invisible to another tenant's session; of N concurrent accepts exactly one claims it; a
//     released claim can be taken again; a used invitation can't be revoked; the whole service flow on the EF store
//     refuses a second use and the wrong address.
//   Gate: RUN_AGENTOS_E2E_REAL=true and AGENTOS_PG_CONN (a server the test may CREATE / DROP DATABASE on). Zero residue.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Invitations;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.Modules.Tenants.Persistence;
using AgentOs.Modules.Tenants.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using AgentOs.Tests.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class AuditAndInvitationsRealPostgresTests
{
    private const string A = "chain-tenant-a";
    private const string B = "chain-tenant-b";

    [Fact]
    public async Task AuditChain_OnRealPostgres_AsTheAppRole_IsGapFree_TenantBound_AndTamperEvident()
    {
        var server = RowLevelSecurityStageTwoRealPostgresTests.ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        await WithTenantsDatabaseAsync(server, async (cs, options) =>
        {
            for (var i = 0; i < 3; i++) { await WriteAsync(options, Entry(A, $"sequential {i}")); }
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => WriteAsync(options, Entry(A, $"concurrent {i}")))));
            await WriteAsync(options, Entry(B, "b1"));
            await WriteAsync(options, Entry(B, "b2"));
            await WriteAsync(options, Entry(AuditScopes.Platform, "tenant.purged: x"));

            (await VerifyAsync(options, A)).ShouldSatisfyAllConditions(
                r => r.Intact.ShouldBeTrue(r.Summary),
                r => r.Verified.ShouldBe(15),
                r => r.HeadHash.ShouldNotBeNull());
            (await VerifyAsync(options, B)).Verified.ShouldBe(2);
            (await VerifyAsync(options, AuditScopes.Platform)).Verified.ShouldBe(1);
            (await SequencesAsync(cs, A)).ShouldBe(LongRange(1, 15));

            // The app path never crosses tenants: a flow bound to B sees none of A's events (RLS, not the WHERE clause).
            await using (var db = new TenantsDbContext(options))
            using (AmbientIdentity.Push(B, null))
            {
                var log = new EfAuditLog(db, NullLogger<EfAuditLog>.Instance);
                (await log.ListAsync(A)).ShouldBeEmpty();
                (await log.ListAsync(B)).Select(e => e.Sequence).ShouldBe([2L, 1L]);
            }

            // Tampering behind the app's back (the owner / superuser — the app role can't) is found at its position.
            await RowLevelSecurityStageTwoRealPostgresTests.ExecAsync(cs,
                $"UPDATE tenants.audit_events SET \"Target\" = 'rewritten' WHERE \"TenantId\" = '{A}' AND \"Sequence\" = 7");
            (await VerifyAsync(options, A)).ShouldSatisfyAllConditions(
                r => r.Problem.ShouldBe(AuditChainProblem.Altered),
                r => r.BrokenAt.ShouldBe(7));
            await RowLevelSecurityStageTwoRealPostgresTests.ExecAsync(cs,
                $"DELETE FROM tenants.audit_events WHERE \"TenantId\" = '{B}' AND \"Sequence\" = 1");
            (await VerifyAsync(options, B)).ShouldSatisfyAllConditions(
                r => r.Problem.ShouldBe(AuditChainProblem.Missing),
                r => r.BrokenAt.ShouldBe(1));

            // The app role may INSERT: a row with no chain link, added after the platform chain started, breaks it.
            await using (var forger = new TenantsDbContext(options))
            using (RowLevelSecurity.PlatformScope("test-forgery"))
            {
                await forger.Database.ExecuteSqlAsync($"""
                    INSERT INTO tenants.audit_events ("Id","TenantId","UserId","Action","Target","TimestampUtc")
                    VALUES ({Guid.NewGuid()}, {AuditScopes.Platform}, 'eve', 'tenant.purged', 'forged', now())
                    """);
            }
            (await VerifyAsync(options, AuditScopes.Platform)).ShouldSatisfyAllConditions(
                r => r.Problem.ShouldBe(AuditChainProblem.Inserted),
                r => r.BrokenAt.ShouldBe(2));
        });
    }

    [Fact]
    public async Task InvitationStore_OnRealPostgres_IsTenantBound_SingleUse_UnderConcurrency()
    {
        var server = RowLevelSecurityStageTwoRealPostgresTests.ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        await WithTenantsDatabaseAsync(server, async (_, options) =>
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            using (RowLevelSecurity.TenantScope(A))
            {
                await using var db = new TenantsDbContext(options);
                await new EfInvitationStore(db).AddAsync(new InvitationRecord(id, A, "bob@acme.test", "bob@acme.test", "member", "admin-1", now, now.AddHours(1)));
            }

            using (RowLevelSecurity.TenantScope(B))
            {
                await using var db = new TenantsDbContext(options);
                (await new EfInvitationStore(db).GetAsync(A, id)).ShouldBeNull();   // RLS: B's session can't see A's row
                (await new EfInvitationStore(db).TryClaimAsync(A, id, now)).ShouldBeFalse();
            }

            using (RowLevelSecurity.TenantScope(A))
            {
                var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                {
                    await using var db = new TenantsDbContext(options);
                    return await new EfInvitationStore(db).TryClaimAsync(A, id, now);
                })));
                claims.Count(won => won).ShouldBe(1);

                await using var store = new TenantsDbContext(options);
                var invitations = new EfInvitationStore(store);
                await invitations.ReleaseAsync(A, id);                       // the account creation failed: give it back
                (await invitations.GetAsync(A, id))!.StatusAt(now).ShouldBe(InvitationStatus.Pending);
                (await invitations.TryClaimAsync(A, id, now)).ShouldBeTrue();
                await invitations.CompleteAsync(A, id, "kc-bob");
                await invitations.ReleaseAsync(A, id);                       // bound to an account: no longer releasable
                var used = await invitations.GetAsync(A, id);
                used!.AcceptedUserId.ShouldBe("kc-bob");
                used.StatusAt(now).ShouldBe(InvitationStatus.Accepted);
                (await invitations.TryRevokeAsync(A, id, now, "admin-1")).ShouldBeFalse();
                (await invitations.TryClaimAsync(A, id, now.AddMinutes(1))).ShouldBeFalse();
            }
        });
    }

    [Fact]
    public async Task SignupService_OnRealPostgres_EfStore_RefusesReuseAndTheWrongAddress()
    {
        var server = RowLevelSecurityStageTwoRealPostgresTests.ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        await WithTenantsDatabaseAsync(server, async (_, options) =>
        {
            var repo = Substitute.For<ITenantsRepository>();
            repo.GetAsync(A, Arg.Any<CancellationToken>()).Returns(new TenantRecord(A, A, DateTimeOffset.UtcNow));
            var kc = Substitute.For<IKeycloakAdminClient>();
            kc.CreateUserAsync(default!, default!, default!, default!, default, default, default).ReturnsForAnyArgs("kc-user");
            var dp = new EphemeralDataProtectionProvider();

            // Minted by A's admin (a flow bound to A) …
            InvitationToken invite;
            await using (var adminScope = new TenantsDbContext(options))
            using (AmbientIdentity.Push(A, "admin-1"))
            {
                invite = await Service(adminScope, repo, kc, dp).CreateInvitationAsync(A, "member", "Bob@Acme.test", TimeSpan.FromHours(1), new AuditActor("admin-1"));
            }

            // … opened and used by an anonymous visitor (no tenant of its own): the signed token binds RLS to A.
            await using var visitorScope = new TenantsDbContext(options);
            var visitor = Service(visitorScope, repo, kc, dp);
            (await visitor.PreviewInvitationAsync(invite.Token))!.Status.ShouldBe(InvitationStatus.Pending);
            await Should.ThrowAsync<InvalidOperationException>(() => visitor.SignupAsync(
                new TenantSignupRequest("eve", "Sup3rSecure!Pass", "eve@evil.test", null, null, invite.Token)));
            (await visitor.SignupAsync(
                new TenantSignupRequest("bob", "Sup3rSecure!Pass", "bob@acme.test", null, null, invite.Token))).TenantId.ShouldBe(A);
            var again = await Should.ThrowAsync<InvalidOperationException>(() => visitor.SignupAsync(
                new TenantSignupRequest("bob2", "Sup3rSecure!Pass", "bob@acme.test", null, null, invite.Token)));
            again.Message.ShouldBe(InvitationRules.Describe(InvitationAcceptance.AlreadyUsed));
            await kc.Received(1).CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

            // Everything it did is on A's chain, intact (minted, refused, signup + accepted, refused).
            var report = await VerifyAsync(options, A);
            report.Intact.ShouldBeTrue(report.Summary);
            report.Verified.ShouldBe(5);
        });
    }

    // ── helpers ──

    // The service as a request scope wires it: the audit log and the invitation store share the scope's context.
    private static TenantSignupService Service(TenantsDbContext db, ITenantsRepository repo, IKeycloakAdminClient kc, IDataProtectionProvider dp)
        => new(repo, kc, new EfAuditLog(db, NullLogger<EfAuditLog>.Instance), dp, NullLogger<TenantSignupService>.Instance,
            settings: null, new EfInvitationStore(db));

    private static IEnumerable<long> LongRange(long start, int count)
    {
        for (var i = 0; i < count; i++) { yield return start + i; }
    }

    private static async Task WithTenantsDatabaseAsync(string server, Func<string, DbContextOptions<TenantsDbContext>, Task> body)
    {
        var database = "agentos_chain_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        var cs = new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString;
        await RowLevelSecurityStageTwoRealPostgresTests.ExecAsync(admin, $"CREATE DATABASE \"{database}\"");
        try
        {
            await using (var db = new TenantsDbContext(RowLevelSecurityStageTwoRealPostgresTests.Options<TenantsDbContext>(cs, "tenants")))
            {
                await db.Database.MigrateAsync();
            }
            await using (var conn = new NpgsqlConnection(cs))
            {
                await conn.OpenAsync();
                await RowLevelSecurityStageTwoRealPostgresTests.ExecAsync(conn, RowLevelSecurity.BootstrapSql(conn.PostgreSqlVersion.Major));
            }
            var options = new DbContextOptionsBuilder<TenantsDbContext>()
                .UseNpgsql(cs, n => n.MigrationsHistoryTable("__EFMigrationsHistory", "tenants"))
                .AddInterceptors(new AppRole())
                .Options;
            await body(cs, options);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await RowLevelSecurityStageTwoRealPostgresTests.ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static AuditEntry Entry(string tenant, string target)
        => new(Guid.NewGuid(), tenant, "user-1", AuditActions.SettingChanged, target, "203.0.113.7", "tests", DateTimeOffset.UtcNow);

    private static async Task WriteAsync(DbContextOptions<TenantsDbContext> options, AuditEntry entry)
    {
        await using var db = new TenantsDbContext(options);
        await new EfAuditLog(db, NullLogger<EfAuditLog>.Instance).WriteAsync(entry);
    }

    private static async Task<AuditChainReport> VerifyAsync(DbContextOptions<TenantsDbContext> options, string tenant)
    {
        await using var db = new TenantsDbContext(options);
        return await new EfAuditLog(db, NullLogger<EfAuditLog>.Instance).VerifyAsync(tenant);
    }

    private static async Task<List<long>> SequencesAsync(string cs, string tenant)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT \"Sequence\" FROM tenants.audit_events WHERE \"TenantId\" = @t ORDER BY \"Sequence\"", conn);
        cmd.Parameters.AddWithValue("t", tenant);
        var list = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { list.Add(reader.GetInt64(0)); }
        return list;
    }

    // Binds every connection as an RLS-enabled host does — without flipping the process-wide RowLevelSecurity.State.
    private sealed class AppRole : DbConnectionInterceptor
    {
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
            => RowLevelSecurity.ApplySessionAsync((NpgsqlConnection)connection, switchRole: true, cancellationToken);

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
            => RowLevelSecurity.ApplySessionAsync((NpgsqlConnection)connection, switchRole: true).GetAwaiter().GetResult();
    }
}
