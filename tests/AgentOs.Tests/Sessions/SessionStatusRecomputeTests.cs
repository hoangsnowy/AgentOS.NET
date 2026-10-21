// A task's status follows its repos: a merged proposed change must read Done, not "Ready for review" forever.

using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class SessionStatusRecomputeTests
{
    private const string Tenant = "t1";

    [Theory]
    [InlineData(new[] { "Merged" }, "Merged")]
    [InlineData(new[] { "Merged", "Merged" }, "Merged")]
    [InlineData(new[] { "Merged", "Done" }, "Done")]
    [InlineData(new[] { "Done", "Done" }, "Done")]
    [InlineData(new[] { "Merged", "Pending" }, "Running")]
    [InlineData(new[] { "Merged", "Failed" }, "Failed")]
    [InlineData(new[] { "Accepted", "Rejected" }, "Done")]       // tested proposed changes are still open changes
    [InlineData(new[] { "Accepted", "Testing" }, "Running")]     // the Testing agent at work
    public async Task Recompute_RepoStatuses_SessionStatus(string[] repoStatuses, string expected)
    {
        var options = new DbContextOptionsBuilder<SessionsDbContext>().UseInMemoryDatabase($"recompute-{Guid.NewGuid()}").Options;
        var sessionId = Guid.NewGuid();
        await using (var db = new SessionsDbContext(options))
        {
            db.Sessions.Add(new RemoteSessionEntity { Id = sessionId, TenantId = Tenant, Title = "#7 t", Status = "Running", CreatedAtUtc = DateTimeOffset.UtcNow });
            foreach (var status in repoStatuses)
            {
                db.SessionRepos.Add(new SessionRepoEntity { Id = Guid.NewGuid(), TenantId = Tenant, SessionId = sessionId, Owner = "o", Repo = "r" + Guid.NewGuid().ToString("N")[..4], Status = status });
            }
            await db.SaveChangesAsync();
        }

        await using (var db = new SessionsDbContext(options))
        {
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns(Tenant);
            (await new SessionRepository(db, tenant).RecomputeSessionStatusForTenantAsync(Tenant, sessionId)).ShouldBeTrue();
        }

        await using (var db = new SessionsDbContext(options))
        {
            (await db.Sessions.IgnoreQueryFilters().SingleAsync(s => s.Id == sessionId)).Status.ShouldBe(expected);
        }
    }
}
