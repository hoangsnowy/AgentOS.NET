// Metrics first for the Board's agents: inside an LlmCallScope every call is recorded, and an agent run is stored so
// Insights counts it (Insights showed nothing while Autopilot's agents worked all day).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Pipeline.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Metrics;

public sealed class AgentRunMetricsTests
{
    private static LlmRequest Req(string? role = "Reviewer") => new("sys", "user", "claude-sonnet-4-6") { AgentRole = role };

    [Fact]
    public void Scope_InnermostWins_AndRestoresOnDispose()
    {
        LlmCallScope.Current.ShouldBeNull();
        using (var outer = LlmCallScope.Begin())
        {
            using (var inner = LlmCallScope.Begin())
            {
                LlmCallScope.Current.ShouldBeSameAs(inner);
            }
            LlmCallScope.Current.ShouldBeSameAs(outer);
        }
        LlmCallScope.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Observed_RecordsASuccessfulCall()
    {
        var inner = Substitute.For<ILlmClient>();
        inner.Provider.Returns("RemoteAgent");
        inner.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse("ok", 1200, 300, 0m, TimeSpan.FromSeconds(42), "claude-sonnet-4-6", "RemoteAgent") { CacheReadTokens = 100 });
        using var scope = LlmCallScope.Begin();

        await new ObservedLlmClient(inner, scope).SendAsync(Req());

        var call = scope.Calls.ShouldHaveSingleItem();
        call.AgentRole.ShouldBe("Reviewer");
        call.InputTokens.ShouldBe(1200);
        call.CacheReadTokens.ShouldBe(100);
        call.Provider.ShouldBe("RemoteAgent");
        call.Latency.ShouldBe(TimeSpan.FromSeconds(42));
        call.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Observed_RecordsAFailedCall_AndRethrows()
    {
        var inner = Substitute.For<ILlmClient>();
        inner.Provider.Returns("RemoteAgent");
        inner.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("runner timed out"));
        using var scope = LlmCallScope.Begin();

        await Should.ThrowAsync<TimeoutException>(() => new ObservedLlmClient(inner, scope).SendAsync(Req()));

        var call = scope.Calls.ShouldHaveSingleItem();
        call.Success.ShouldBeFalse();
        call.Error.ShouldBe("runner timed out");
    }

    private sealed class FixedTenant(string id) : ITenantContext
    {
        public string TenantId { get; } = id;
        public string? UserId => "u";
        public string? UserName => "u";
        public IReadOnlyList<string> Roles { get; } = [];
        public bool IsAuthenticated => true;
        public bool IsAdmin => false;
    }

    [Fact]
    public async Task SaveAgentRun_IsCountedByTheCostSummary()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>().UseInMemoryDatabase($"agentrun-{Guid.NewGuid()}").Options;
        var now = DateTimeOffset.UtcNow;
        LlmCallRecord Call(string? role, int input, bool ok = true) =>
            new(role, "claude-sonnet-4-6", "RemoteAgent", input, 100, 0, 0, 0, 0m, TimeSpan.FromSeconds(3), ok, ok ? null : "x", now);

        await using (var db = new PipelineDbContext(options, new FixedTenant("t1")))
        {
            await new PipelineRunRepository(db, new FixedTenant("t1")).SaveAgentRunAsync(
                new AgentRunRecord(Guid.NewGuid(), "Coding · #15 Build SVG game viewer", "Coding", true, now, now, [Call(null, 1000), Call("Reviewer", 500)]), "t1");
        }

        await using var read = new PipelineDbContext(options, new FixedTenant("x"));
        var repo = new PipelineRunRepository(read, new FixedTenant("x"));
        var sum = await repo.GetCostSummaryForTenantAsync("t1");

        sum.CallCount.ShouldBe(2);
        sum.RunCount.ShouldBe(1);
        sum.TotalTokensIn.ShouldBe(1500);
        sum.ByAgent.Select(b => b.Key).ShouldBe(["Coding", "Reviewer"], ignoreOrder: true);
        (await repo.ListForTenantAsync("t1")).ShouldHaveSingleItem().UserStoryPreview.ShouldContain("#15");
    }
}
