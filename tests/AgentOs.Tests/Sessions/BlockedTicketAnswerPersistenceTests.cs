// What makes "a maintainer's answer resumes a blocked task" idempotent: the time a task became Blocked (stamped by the
// status roll-up on the transition) and the newest answer already acted on (never moved back).

using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class BlockedTicketAnswerPersistenceTests
{
    private const string Tenant = "t1";

    private static SessionRepository Repo(SessionsDbContext db)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(Tenant);
        return new SessionRepository(db, tenant);
    }

    private static DbContextOptions<SessionsDbContext> Options() =>
        new DbContextOptionsBuilder<SessionsDbContext>().UseInMemoryDatabase($"blocked-{Guid.NewGuid()}").Options;

    private static async Task<(Guid Session, Guid Repo)> SeedAsync(DbContextOptions<SessionsDbContext> options, string status, string repoStatus)
    {
        var session = Guid.NewGuid();
        var repo = Guid.NewGuid();
        await using var db = new SessionsDbContext(options);
        db.Sessions.Add(new RemoteSessionEntity { Id = session, TenantId = Tenant, Title = "#42", Status = status, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1) });
        db.SessionRepos.Add(new SessionRepoEntity { Id = repo, TenantId = Tenant, SessionId = session, Owner = "o", Repo = "r", Status = repoStatus });
        await db.SaveChangesAsync();
        return (session, repo);
    }

    private static async Task<RemoteSessionEntity> RowAsync(DbContextOptions<SessionsDbContext> options, Guid id)
    {
        await using var db = new SessionsDbContext(options);
        return await db.Sessions.IgnoreQueryFilters().SingleAsync(s => s.Id == id);
    }

    [Fact]
    public async Task Recompute_BecomingBlocked_StampsTheTime_AndKeepsItWhileBlocked()
    {
        var options = Options();
        var (session, repo) = await SeedAsync(options, "Done", "ChangesRequested");
        var before = DateTimeOffset.UtcNow;

        await using (var db = new SessionsDbContext(options))
        {
            var r = Repo(db);
            await r.UpdateRepoRunResultAsync(Tenant, repo, "Blocked", "b", "https://github.com/o/r/pull/7", "Code review still asks for changes after 3 automatic fixes — needs a person.");
            await r.RecomputeSessionStatusForTenantAsync(Tenant, session);
        }
        var blocked = await RowAsync(options, session);
        blocked.Status.ShouldBe("Blocked");
        blocked.BlockedAtUtc.ShouldNotBeNull();
        blocked.BlockedAtUtc!.Value.ShouldBeGreaterThanOrEqualTo(before);

        // Blocked again on a later pass (nothing changed): the time it became Blocked stays.
        await using (var db = new SessionsDbContext(options))
        {
            await Repo(db).RecomputeSessionStatusForTenantAsync(Tenant, session);
        }
        (await RowAsync(options, session)).BlockedAtUtc.ShouldBe(blocked.BlockedAtUtc);
    }

    [Fact]
    public async Task Recompute_ResumedThenBlockedAgain_StampsANewTime()
    {
        var options = Options();
        var (session, repo) = await SeedAsync(options, "Blocked", "Blocked");
        var old = DateTimeOffset.UtcNow.AddHours(-5);
        await using (var db = new SessionsDbContext(options))
        {
            var row = await db.Sessions.IgnoreQueryFilters().SingleAsync(s => s.Id == session);
            row.BlockedAtUtc = old;
            await db.SaveChangesAsync();
        }

        await using (var db = new SessionsDbContext(options))
        {
            var r = Repo(db);
            await r.UpdateRunResultAsync(Tenant, session, "Running", null, null);   // the resumed run
            await r.UpdateRepoRunResultAsync(Tenant, repo, "Blocked", "b", "https://github.com/o/r/pull/7", "needs a person");
            await r.RecomputeSessionStatusForTenantAsync(Tenant, session);
        }

        (await RowAsync(options, session)).BlockedAtUtc!.Value.ShouldBeGreaterThan(old);
    }

    [Fact]
    public async Task Recompute_NotBlocked_StampsNothing()
    {
        var options = Options();
        var (session, _) = await SeedAsync(options, "Running", "Done");

        await using (var db = new SessionsDbContext(options))
        {
            await Repo(db).RecomputeSessionStatusForTenantAsync(Tenant, session);
        }

        var row = await RowAsync(options, session);
        row.Status.ShouldBe("Done");
        row.BlockedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task MarkTicketAnswer_RecordsTheNewest_NeverMovesBack_OnlyForItsTenant()
    {
        var options = Options();
        var (session, _) = await SeedAsync(options, "Blocked", "Blocked");
        var first = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

        await using (var db = new SessionsDbContext(options))
        {
            var r = Repo(db);
            await r.MarkTicketAnswerForTenantAsync(Tenant, session, first);
            await r.MarkTicketAnswerForTenantAsync(Tenant, session, first.AddHours(-1));   // an older answer: ignored
            await r.MarkTicketAnswerForTenantAsync("other", session, first.AddHours(5));    // another tenant: no row
        }
        (await RowAsync(options, session)).TicketAnswerAtUtc.ShouldBe(first);

        await using (var db = new SessionsDbContext(options))
        {
            await Repo(db).MarkTicketAnswerForTenantAsync(Tenant, session, first.AddHours(2));
        }
        (await RowAsync(options, session)).TicketAnswerAtUtc.ShouldBe(first.AddHours(2));
    }
}
