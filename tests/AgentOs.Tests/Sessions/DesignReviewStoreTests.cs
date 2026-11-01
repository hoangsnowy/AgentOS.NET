// Design gate (ADR-0016) — where its data lives, kept apart per tenant: the screenshots in the tenant-scoped file store
// under a design scope (per board, ticket and test run), the reviews in sessions.design_reviews (one per task and run,
// the owner's decision on the row). The in-memory stores (standalone desktop) and the EF stores on a REAL Postgres
// (migration, unique (tenant, task, run), RLS binding the new table) both keep two tenants from seeing each other.
//   Real-Postgres gate: RUN_AGENTOS_E2E_REAL=true and AGENTOS_PG_CONN (a user that may CREATE DATABASE).

using System;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class DesignReviewStoreTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7];

    private static DesignReview Review(string tenant, Guid board, Guid session, int run, DesignReviewStatus status = DesignReviewStatus.Changes) =>
        DesignReview.New(tenant, board, session, 7, run, status) with
        {
            Summary = "Unreadable button.",
            Findings = [new DesignFinding("Checkout · phone · dark", "Unreadable", "Use the accent token")],
            Shots = [new DesignShot(".agentos/screenshots/checkout-390-dark.png", "Checkout", 390, "dark")],
        };

    private static StoredFileInfo Shot(string tenant, string scope, string path) =>
        new(Guid.NewGuid(), tenant, scope, path, Png.Length, RunFile.Hash(Png), "image/png", "Testing agent", DateTimeOffset.UtcNow);

    // ── In memory (the standalone desktop) ──

    [Fact]
    public async Task InMemory_ReviewsAndScreenshots_AreKeptApartPerTenant()
    {
        var reviews = new InMemoryDesignReviewStore();
        var files = new InMemoryScopedFileStore();
        var board = Guid.NewGuid();
        var session = Guid.NewGuid();
        var scope = DesignScope.For(board, 7, 0);

        await reviews.SaveAsync(Review("tenant-a", board, session, 0));
        await files.PutAsync(Shot("tenant-a", scope, ".agentos/screenshots/checkout-390-dark.png"), Png);

        (await reviews.GetAsync("tenant-b", session, 0)).ShouldBeNull();
        (await reviews.LatestAsync("tenant-b", session)).ShouldBeNull();
        (await reviews.ListRecentAsync("tenant-b")).ShouldBeEmpty();
        (await files.ListAsync("tenant-b", scope)).ShouldBeEmpty();
        (await files.GetAsync("tenant-b", scope, ".agentos/screenshots/checkout-390-dark.png")).ShouldBeNull();

        var a = (await reviews.GetAsync("tenant-a", session, 0)).ShouldNotBeNull();
        a.Findings.ShouldHaveSingleItem().Fix.ShouldBe("Use the accent token");
        a.Shots.ShouldHaveSingleItem().IsDark.ShouldBeTrue();
        a.ScopeKey.ShouldBe(scope);
        (await files.GetAsync("tenant-a", scope, ".agentos/screenshots/checkout-390-dark.png"))!.Content.ShouldBe(Png);
    }

    [Fact]
    public async Task InMemory_OneReviewPerTaskAndRun_TheLatestRunWins_AndTheDecisionIsKept()
    {
        var reviews = new InMemoryDesignReviewStore();
        var board = Guid.NewGuid();
        var session = Guid.NewGuid();

        var first = await reviews.SaveAsync(Review("t", board, session, 0));
        var decided = await reviews.SaveAsync(first with { Decision = DesignDecision.Approved, DecidedBy = "Ann", DecidedAtUtc = DateTimeOffset.UtcNow });
        decided.Id.ShouldBe(first.Id);                                         // a replace, not a second row
        await reviews.SaveAsync(Review("t", board, session, 1, DesignReviewStatus.Pending));

        (await reviews.GetAsync("t", session, 0))!.Gate.ShouldBe(DesignGateState.Passed);
        (await reviews.LatestAsync("t", session))!.Run.ShouldBe(1);
        (await reviews.ListRecentAsync("t")).Count.ShouldBe(2);
    }

    // ── Real Postgres ──

    private static string? ServerConnection =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E_REAL"), "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("AGENTOS_PG_CONN")
            : null;

    [Fact]
    public async Task Ef_OnRealPostgres_IsTenantIsolated_OnePerTaskAndRun_AndRlsBindsTheTable()
    {
        var server = ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        var database = "agentos_design_" + Guid.NewGuid().ToString("N")[..12];
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
            var scope = DesignScope.For(board, 7, 0);
            await using (var db = new SessionsDbContext(options))
            {
                var reviews = new EfDesignReviewStore(db);
                var files = new EfScopedFileStore(db);
                var saved = await reviews.SaveAsync(Review("tenant-a", board, session, 0));
                await reviews.SaveAsync(saved with { Decision = DesignDecision.ChangesAsked, DecisionNote = "Stack it on the phone.", DecidedBy = "Ann" });
                await reviews.SaveAsync(Review("tenant-b", board, session, 0, DesignReviewStatus.Pass));
                await files.PutAsync(Shot("tenant-a", scope, ".agentos/screenshots/checkout-390-dark.png"), Png);
            }
            await using (var db = new SessionsDbContext(options))
            {
                var reviews = new EfDesignReviewStore(db);
                var files = new EfScopedFileStore(db);
                var a = (await reviews.GetAsync("tenant-a", session, 0)).ShouldNotBeNull();
                a.Gate.ShouldBe(DesignGateState.ChangesRequested);
                a.DecisionNote.ShouldBe("Stack it on the phone.");
                a.Findings.ShouldHaveSingleItem().Screen.ShouldBe("Checkout · phone · dark");
                a.Shots.ShouldHaveSingleItem().Width.ShouldBe(390);
                (await reviews.GetAsync("tenant-b", session, 0))!.Gate.ShouldBe(DesignGateState.Passed);
                (await reviews.ListRecentAsync("tenant-a")).ShouldHaveSingleItem();
                (await files.ListAsync("tenant-b", scope)).ShouldBeEmpty();
                (await files.GetAsync("tenant-a", scope, ".agentos/screenshots/checkout-390-dark.png"))!.Content.ShouldBe(Png);
            }

            // The unique (tenant, task, run) index refuses a raw twin row.
            var twin = await Should.ThrowAsync<PostgresException>(() => Exec(cs, $"""
                INSERT INTO sessions.design_reviews ("Id","TenantId","BoardId","SessionId","IssueNumber","Run","Status","Summary","FindingsJson","ShotsJson","UpdatedAtUtc")
                VALUES (gen_random_uuid(), 'tenant-a', '{board}', '{session}', 7, 0, 'Pending', '', '[]', '[]', now())
                """));
            twin.SqlState.ShouldBe("23505");

            // RLS: as the app role with tenant A set, no tenant predicate sees only A's review; a row for B is refused.
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await Exec(conn, RowLevelSecurity.BootstrapSql(conn.PostgreSqlVersion.Major));
            await Exec(conn, $"SET ROLE {RowLevelSecurity.Role}; SELECT set_config('{RowLevelSecurity.TenantSetting}', 'tenant-a', false)");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sessions.design_reviews";
                ((long)(await cmd.ExecuteScalarAsync())!).ShouldBe(1L);
            }
            var refused = await Should.ThrowAsync<PostgresException>(() => Exec(conn, $"""
                INSERT INTO sessions.design_reviews ("Id","TenantId","BoardId","SessionId","IssueNumber","Run","Status","Summary","FindingsJson","ShotsJson","UpdatedAtUtc")
                VALUES (gen_random_uuid(), 'tenant-b', '{board}', '{Guid.NewGuid()}', 7, 0, 'Pending', '', '[]', '[]', now())
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
