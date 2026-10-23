// Delivery metrics — the numbers that show whether the AI team improves: merged tasks, first-try rate, fix rounds,
// time to merge, and what waits for a person.

using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using AgentOs.Web.Components.Pages;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class DeliveryMetricsTests
{
    [Fact]
    public void From_ComputesFirstTryAverageAndMedian()
    {
        var d = DeliverySummary.From([(0, 2.0), (2, 10.0), (0, 4.0), (1, 30.0)], stuck: 3);

        d.Merged.ShouldBe(4);
        d.FirstTry.ShouldBe(2);
        d.FirstTryRate.ShouldBe(0.5);
        d.AverageFixRounds.ShouldBe(0.75);
        d.MedianHoursToMerge.ShouldBe(7.0); // (4 + 10) / 2
        d.Stuck.ShouldBe(3);
    }

    [Fact]
    public void From_NothingMerged_KeepsTheStuckCount() =>
        DeliverySummary.From([], stuck: 2).ShouldBe(DeliverySummary.Empty with { Stuck = 2 });

    private const string Tenant = "t1";

    private static SessionRepository Repo(SessionsDbContext db)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(Tenant);
        return new SessionRepository(db, tenant);
    }

    [Fact]
    public async Task Repository_CountsRounds_StampsTheMerge_AndSummarises()
    {
        var options = new DbContextOptionsBuilder<SessionsDbContext>().UseInMemoryDatabase($"delivery-{Guid.NewGuid()}").Options;
        var merged = Guid.NewGuid();
        var stuck = Guid.NewGuid();
        await using (var db = new SessionsDbContext(options))
        {
            db.Sessions.Add(new RemoteSessionEntity { Id = merged, TenantId = Tenant, Title = "#15", Status = "Done", CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-3) });
            db.Sessions.Add(new RemoteSessionEntity { Id = stuck, TenantId = Tenant, Title = "#42", Status = "Blocked", CreatedAtUtc = DateTimeOffset.UtcNow });
            db.Sessions.Add(new RemoteSessionEntity { Id = Guid.NewGuid(), TenantId = "other", Title = "#9", Status = "Failed", CreatedAtUtc = DateTimeOffset.UtcNow });
            db.SessionRepos.Add(new SessionRepoEntity { Id = Guid.NewGuid(), TenantId = Tenant, SessionId = merged, Owner = "o", Repo = "r", Status = "Merged" });
            await db.SaveChangesAsync();
        }

        await using (var db = new SessionsDbContext(options))
        {
            var repo = Repo(db);
            await repo.IncrementFixRoundsForTenantAsync(Tenant, merged);
            await repo.IncrementFixRoundsForTenantAsync(Tenant, merged);
            await repo.RecomputeSessionStatusForTenantAsync(Tenant, merged);
        }

        await using (var db = new SessionsDbContext(options))
        {
            var row = await db.Sessions.IgnoreQueryFilters().SingleAsync(s => s.Id == merged);
            row.FixRounds.ShouldBe(2);
            row.MergedAtUtc.ShouldNotBeNull();

            var d = await Repo(db).GetDeliveryForTenantAsync(Tenant);
            d.Merged.ShouldBe(1);
            d.FirstTry.ShouldBe(0);
            d.AverageFixRounds.ShouldBe(2);
            d.MedianHoursToMerge.ShouldBeInRange(2.9, 3.1);
            d.Stuck.ShouldBe(1); // the other tenant's failed task is not counted
        }
    }

    [Theory]
    [InlineData(0.2, "12 min")]
    [InlineData(3.46, "3.5 h")]
    [InlineData(60, "2.5 days")]
    public void Duration_ReadsNaturally(double hours, string expected) => InsightsApp.Duration(hours).ShouldBe(expected);
}
