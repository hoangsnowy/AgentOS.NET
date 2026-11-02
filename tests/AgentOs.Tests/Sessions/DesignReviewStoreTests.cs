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

    internal static readonly DesignShot[] Shots = [new(".agentos/screenshots/checkout-390-dark.png", "Checkout", 390, "dark")];

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

        await reviews.EnsureAsync(Review("tenant-a", board, session, 0));
        await files.PutAsync(Shot("tenant-a", scope, ".agentos/screenshots/checkout-390-dark.png"), Png);
        (await reviews.SaveVerdictAsync("tenant-b", session, 0, new DesignVerdictUpdate(DesignReviewStatus.Pass))).ShouldBeNull();
        (await reviews.DecideAsync("tenant-b", session, 0, new DesignOwnerDecision(DesignDecision.Approved, null, "Eve", DateTimeOffset.UtcNow))).ShouldBeNull();
        (await reviews.ListForTaskAsync("tenant-b", session)).ShouldBeEmpty();

        (await reviews.GetAsync("tenant-b", session, 0)).ShouldBeNull();
        (await reviews.LatestAsync("tenant-b", session)).ShouldBeNull();
        (await reviews.ListRecentAsync("tenant-b")).ShouldBeEmpty();
        (await files.ListAsync("tenant-b", scope)).ShouldBeEmpty();
        (await files.GetAsync("tenant-b", scope, ".agentos/screenshots/checkout-390-dark.png")).ShouldBeNull();

        var a = (await reviews.GetAsync("tenant-a", session, 0)).ShouldNotBeNull();
        a.Findings.ShouldHaveSingleItem().Fix.ShouldBe("Use the accent token");
        a.Shots.ShouldHaveSingleItem().IsDark.ShouldBeTrue();
        a.ScopeKey.ShouldBe(scope);
        a.Gate.ShouldBe(DesignGateState.ChangesRequested);                    // nothing tenant B tried landed
        (await files.GetAsync("tenant-a", scope, ".agentos/screenshots/checkout-390-dark.png"))!.Content.ShouldBe(Png);
    }

    [Fact]
    public async Task InMemory_OneReviewPerTaskAndRun_EnsureKeepsTheExisting_TheLatestRunWins()
    {
        var reviews = new InMemoryDesignReviewStore();
        var board = Guid.NewGuid();
        var session = Guid.NewGuid();

        var first = await reviews.EnsureAsync(Review("t", board, session, 0));
        await reviews.DecideAsync("t", session, 0, new DesignOwnerDecision(DesignDecision.Approved, null, "Ann", DateTimeOffset.UtcNow));
        var again = await reviews.EnsureAsync(Review("t", board, session, 0, DesignReviewStatus.Pending));
        again.Id.ShouldBe(first.Id);                                           // the existing row, untouched
        again.Decision.ShouldBe(DesignDecision.Approved);
        again.Status.ShouldBe(DesignReviewStatus.Changes);
        await reviews.EnsureAsync(Review("t", board, session, 1, DesignReviewStatus.Pending));

        (await reviews.GetAsync("t", session, 0))!.Gate.ShouldBe(DesignGateState.Passed);
        (await reviews.LatestAsync("t", session))!.Run.ShouldBe(1);
        (await reviews.ListForTaskAsync("t", session)).Select(r => r.Run).ShouldBe([1, 0]);
        (await reviews.ListRecentAsync("t")).Count.ShouldBe(2);
    }

    // ── Lost updates: the agent's side and the owner's decision never overwrite each other ──

    // The race the security review found: the check starts (Decision null), the owner presses "Ask for changes" while the
    // Designer runs (up to 30 minutes), then the Designer returns PASS. The decision must stand — or the ticket merges.
    internal static async Task DecisionMadeWhileTheCheckRuns_SurvivesAPassVerdict(IDesignReviewStore reviews, string tenant, Guid board, Guid session)
    {
        await reviews.EnsureAsync(DesignReview.New(tenant, board, session, 7, 0, DesignReviewStatus.Pending) with { Shots = Shots });
        var started = (await reviews.SaveVerdictAsync(tenant, session, 0, new DesignVerdictUpdate(DesignReviewStatus.Checking)))!;
        started.Decision.ShouldBeNull();                                       // what the running check holds

        await reviews.DecideAsync(tenant, session, 0, new DesignOwnerDecision(DesignDecision.ChangesAsked, "Stack the form on the phone.", "Ann", DateTimeOffset.UtcNow));
        var after = await reviews.SaveVerdictAsync(tenant, session, started.Run, new DesignVerdictUpdate(DesignReviewStatus.Pass, "Looks right.", [], Shots));

        after!.Status.ShouldBe(DesignReviewStatus.Pass);                       // the verdict is recorded…
        after.Decision.ShouldBe(DesignDecision.ChangesAsked);                  // …and the owner's decision stands
        after.DecisionNote.ShouldBe("Stack the form on the phone.");
        after.DecidedBy.ShouldBe("Ann");
        after.Gate.ShouldBe(DesignGateState.ChangesRequested);                 // so Autopilot sends it back, never merges
        (await reviews.GetAsync(tenant, session, 0))!.Gate.ShouldBe(DesignGateState.ChangesRequested);
    }

    // The other way round: a decision taken on a page that shows an older copy (DesignChecks caches for 30 s) must not
    // erase a verdict that landed since.
    internal static async Task ADecisionFromAStalePage_KeepsTheVerdict(IDesignReviewStore reviews, string tenant, Guid board, Guid session)
    {
        var stale = await reviews.EnsureAsync(DesignReview.New(tenant, board, session, 7, 0, DesignReviewStatus.Pending) with { Shots = Shots });
        await reviews.SaveVerdictAsync(tenant, session, 0, new DesignVerdictUpdate(DesignReviewStatus.Changes, "Two problems.",
            [new DesignFinding("Checkout · phone · dark", "Unreadable", "Accent token")], Shots));

        var decided = await AgentOs.Web.Services.BoardTasks.BoardTaskService.DecideAsync(reviews, stale, DesignDecision.Approved, null, "Ann");

        decided!.Status.ShouldBe(DesignReviewStatus.Changes);
        decided.Findings.ShouldHaveSingleItem();
        decided.Summary.ShouldBe("Two problems.");
        decided.Gate.ShouldBe(DesignGateState.Passed);
    }

    // ── No blind approvals: nobody decides on what they can't see, and an approval stands only on its screens ──

    internal static async Task NoDecisionWithoutScreenshots_AndNewScreenshotsClearAnApproval(IDesignReviewStore reviews, string tenant, Guid board)
    {
        var now = DateTimeOffset.UtcNow;

        // Nothing to look at yet (the Testing agent hasn't run): both decisions are refused, in plain words; nothing stored.
        var task = Guid.NewGuid();
        await reviews.EnsureAsync(DesignReview.New(tenant, board, task, 7, 0, DesignReviewStatus.Pending));
        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            reviews.DecideAsync(tenant, task, 0, new DesignOwnerDecision(DesignDecision.Approved, null, "Ann", now)));
        refused.Message.ShouldBe(DesignReview.NothingToDecideYet);
        await Should.ThrowAsync<InvalidOperationException>(() =>
            reviews.DecideAsync(tenant, task, 0, new DesignOwnerDecision(DesignDecision.ChangesAsked, "Stack it.", "Ann", now)));
        var untouched = (await reviews.GetAsync(tenant, task, 0))!;
        untouched.Decision.ShouldBeNull();
        untouched.Gate.ShouldBe(DesignGateState.NotChecked);

        // The screenshots arrive; the owner approves what they see — and a Designer verdict on the same screens keeps it
        // (the approve-after-screenshots path is unchanged: the owner's decision wins over the agent).
        await reviews.SaveVerdictAsync(tenant, task, 0, new DesignVerdictUpdate(DesignReviewStatus.Pending, string.Empty, [], Shots, NewCapture: true));
        (await reviews.DecideAsync(tenant, task, 0, new DesignOwnerDecision(DesignDecision.Approved, null, "Ann", now)))!.Gate.ShouldBe(DesignGateState.Passed);
        var verdict = (await reviews.SaveVerdictAsync(tenant, task, 0, new DesignVerdictUpdate(DesignReviewStatus.Changes, "Unreadable.", [], Shots)))!;
        verdict.Decision.ShouldBe(DesignDecision.Approved);
        verdict.Gate.ShouldBe(DesignGateState.Passed);

        // A re-test sends new screenshots: the approval was given on other screens, so it is cleared — the owner decides again.
        var recaptured = (await reviews.SaveVerdictAsync(tenant, task, 0, new DesignVerdictUpdate(DesignReviewStatus.Pending, string.Empty, [], Shots, NewCapture: true)))!;
        recaptured.Decision.ShouldBeNull();
        recaptured.DecidedBy.ShouldBeNull();
        recaptured.DecidedAtUtc.ShouldBeNull();
        recaptured.Gate.ShouldBe(DesignGateState.NotChecked);

        // "Ask for changes" is never cleared by new screenshots: it can only hold a merge back.
        await reviews.DecideAsync(tenant, task, 0, new DesignOwnerDecision(DesignDecision.ChangesAsked, "Stack it.", "Ann", now));
        var kept = (await reviews.SaveVerdictAsync(tenant, task, 0, new DesignVerdictUpdate(DesignReviewStatus.Pending, string.Empty, [], Shots, NewCapture: true)))!;
        kept.Decision.ShouldBe(DesignDecision.ChangesAsked);
        kept.Gate.ShouldBe(DesignGateState.ChangesRequested);

        // A blind approval stored before the refusal existed (the lab's #122 / #123: Pending + Approved, no screenshots):
        // never Passed, and the first screenshots clear it — whoever brings them (here the Designer's verdict).
        var legacy = Guid.NewGuid();
        var row = await reviews.EnsureAsync(DesignReview.New(tenant, board, legacy, 7, 0, DesignReviewStatus.Pending)
            with { Decision = DesignDecision.Approved, DecidedBy = "owner", DecidedAtUtc = now });
        row.Decision.ShouldBe(DesignDecision.Approved);
        row.BlindApproval.ShouldBeTrue();
        row.Gate.ShouldBe(DesignGateState.NotChecked);
        var firstShots = (await reviews.SaveVerdictAsync(tenant, legacy, 0, new DesignVerdictUpdate(DesignReviewStatus.NeedsPerson, "Taste call.", [], Shots)))!;
        firstShots.Decision.ShouldBeNull();
        firstShots.Gate.ShouldBe(DesignGateState.NeedsPerson);
    }

    [Fact]
    public Task InMemory_NoDecisionWithoutScreenshots_AndNewScreenshotsClearAnApproval() =>
        NoDecisionWithoutScreenshots_AndNewScreenshotsClearAnApproval(new InMemoryDesignReviewStore(), "t", Guid.NewGuid());

    [Fact]
    public Task InMemory_DecisionMadeWhileTheCheckRuns_SurvivesAPassVerdict() =>
        DecisionMadeWhileTheCheckRuns_SurvivesAPassVerdict(new InMemoryDesignReviewStore(), "t", Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public Task InMemory_ADecisionFromAStalePage_KeepsTheVerdict() =>
        ADecisionFromAStalePage_KeepsTheVerdict(new InMemoryDesignReviewStore(), "t", Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task Verdict_KeepsWhatItDoesntName_AndCountsRetries()
    {
        var reviews = new InMemoryDesignReviewStore();
        var session = Guid.NewGuid();
        await reviews.EnsureAsync(Review("t", Guid.NewGuid(), session, 0));

        var checking = await reviews.SaveVerdictAsync("t", session, 0, new DesignVerdictUpdate(DesignReviewStatus.Checking));
        checking!.Summary.ShouldBe("Unreadable button.");                      // null fields are kept
        checking.Findings.ShouldHaveSingleItem();
        checking.Shots.ShouldHaveSingleItem();

        var notRun = await reviews.SaveVerdictAsync("t", session, 0, new DesignVerdictUpdate(DesignReviewStatus.NotRun, Error: "No computer."));
        notRun!.CanRetryAutomatically.ShouldBeTrue();
        notRun.Gate.ShouldBe(DesignGateState.NeedsPerson);
        for (var i = 0; i < DesignReview.MaxAutomaticRetries; i++)
        {
            await reviews.SaveVerdictAsync("t", session, 0, new DesignVerdictUpdate(DesignReviewStatus.Checking, CountRetry: true));
            notRun = await reviews.SaveVerdictAsync("t", session, 0, new DesignVerdictUpdate(DesignReviewStatus.NotRun, Error: "No computer."));
        }
        notRun!.Retries.ShouldBe(DesignReview.MaxAutomaticRetries);
        notRun.CanRetryAutomatically.ShouldBeFalse();                          // bounded: now the owner decides

        var decided = await reviews.DecideAsync("t", session, 0, new DesignOwnerDecision(DesignDecision.Approved, null, "Ann", DateTimeOffset.UtcNow));
        decided!.CanRetryAutomatically.ShouldBeFalse();
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
                await reviews.EnsureAsync(Review("tenant-a", board, session, 0));
                await reviews.DecideAsync("tenant-a", session, 0, new DesignOwnerDecision(DesignDecision.ChangesAsked, "Stack it on the phone.", "Ann", DateTimeOffset.UtcNow));
                await reviews.EnsureAsync(Review("tenant-b", board, session, 0, DesignReviewStatus.Pass));
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

            // Lost updates, as they really happen: the check's scope (its own context, holding the row it started with)
            // and the owner's scope (another context) write the same row; neither erases the other's columns.
            var raced = Guid.NewGuid();
            await using (var check = new SessionsDbContext(options))
            {
                var checkStore = new EfDesignReviewStore(check);
                await checkStore.EnsureAsync(DesignReview.New("tenant-a", board, raced, 7, 0, DesignReviewStatus.Pending) with { Shots = Shots });
                (await checkStore.SaveVerdictAsync("tenant-a", raced, 0, new DesignVerdictUpdate(DesignReviewStatus.Checking)))!.Decision.ShouldBeNull();
                await using (var owner = new SessionsDbContext(options))
                {
                    await new EfDesignReviewStore(owner).DecideAsync("tenant-a", raced, 0,
                        new DesignOwnerDecision(DesignDecision.ChangesAsked, "Stack the form on the phone.", "Ann", DateTimeOffset.UtcNow));
                }
                await checkStore.SaveVerdictAsync("tenant-a", raced, 0, new DesignVerdictUpdate(DesignReviewStatus.Pass, "Looks right.", [], Shots));
            }
            await using (var read = new SessionsDbContext(options))
            {
                var after = (await new EfDesignReviewStore(read).GetAsync("tenant-a", raced, 0))!;
                after.Status.ShouldBe(DesignReviewStatus.Pass);
                after.Decision.ShouldBe(DesignDecision.ChangesAsked);
                after.DecisionNote.ShouldBe("Stack the form on the phone.");
                after.Gate.ShouldBe(DesignGateState.ChangesRequested);
            }
            await using (var db = new SessionsDbContext(options))
            {
                await ADecisionFromAStalePage_KeepsTheVerdict(new EfDesignReviewStore(db), "tenant-a", board, Guid.NewGuid());
            }
            await using (var db = new SessionsDbContext(options))
            {
                await NoDecisionWithoutScreenshots_AndNewScreenshotsClearAnApproval(new EfDesignReviewStore(db), "tenant-a", board);
            }

            // The unique (tenant, task, run) index refuses a raw twin row.
            var twin = await Should.ThrowAsync<PostgresException>(() => Exec(cs, $"""
                INSERT INTO sessions.design_reviews ("Id","TenantId","BoardId","SessionId","IssueNumber","Run","Status","Summary","FindingsJson","ShotsJson","Retries","UpdatedAtUtc")
                VALUES (gen_random_uuid(), 'tenant-a', '{board}', '{session}', 7, 0, 'Pending', '', '[]', '[]', 0, now())
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
                ((long)(await cmd.ExecuteScalarAsync())!).ShouldBe(5L);   // tenant A's five reviews; never B's
            }
            var refused = await Should.ThrowAsync<PostgresException>(() => Exec(conn, $"""
                INSERT INTO sessions.design_reviews ("Id","TenantId","BoardId","SessionId","IssueNumber","Run","Status","Summary","FindingsJson","ShotsJson","Retries","UpdatedAtUtc")
                VALUES (gen_random_uuid(), 'tenant-b', '{board}', '{Guid.NewGuid()}', 7, 0, 'Pending', '', '[]', '[]', 0, now())
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
