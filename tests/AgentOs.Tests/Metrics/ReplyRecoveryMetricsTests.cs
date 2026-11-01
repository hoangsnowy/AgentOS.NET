// Metrics first: the one follow-up that asks a run for its missing reply is metered like any call, tagged with its
// purpose, stored with the agent run, and counted by Insights ("Replies asked again").

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Pipeline.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Metrics;

public sealed class ReplyRecoveryMetricsTests
{
    [Fact]
    public async Task Observed_RecordsTheCallsPurpose()
    {
        var inner = Substitute.For<ILlmClient>();
        inner.Provider.Returns("RemoteAgent");
        inner.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse("{}", 10, 5, 0m, TimeSpan.FromSeconds(1), "m", "RemoteAgent"));
        using var scope = LlmCallScope.Begin();
        var client = new ObservedLlmClient(inner, scope);

        await client.SendAsync(new LlmRequest("s", "u", "m") { AgentRole = "Coding" });
        await client.SendAsync(new LlmRequest("s", "u", "m") { AgentRole = "Coding", Purpose = LlmCallPurposes.ReplyRecovery });

        scope.Calls[0].Purpose.ShouldBeNull();
        scope.Calls[1].Purpose.ShouldBe(LlmCallPurposes.ReplyRecovery);
        scope.Calls[1].AgentRole.ShouldBe("Coding");   // still the Coding agent's call — its tokens count there
    }

    [Fact]
    public async Task Insights_CountsTheRepliesAskedAgain_PerTenant()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>().UseInMemoryDatabase($"recovery-{Guid.NewGuid()}").Options;
        var now = DateTimeOffset.UtcNow;
        LlmCallRecord Call(string? purpose) =>
            new("Coding", "computer-default", "RemoteAgent", 1000, 100, 0, 0, 0, 0m, TimeSpan.FromSeconds(3), true, null, now) { Purpose = purpose };

        await using (var db = new PipelineDbContext(options, new Tenant("t1")))
        {
            var repo = new PipelineRunRepository(db, new Tenant("t1"));
            await repo.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), "Coding · #102", "Coding", true, now, now,
                [Call(null), Call(LlmCallPurposes.ReplyRecovery)]), "t1");
            await repo.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), "Coding · #103", "Coding", true, now, now, [Call(null)]), "t1");
        }
        await using (var db = new PipelineDbContext(options, new Tenant("t2")))
        {
            await new PipelineRunRepository(db, new Tenant("t2")).SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), "Coding · #1", "Coding",
                true, now, now, [Call(LlmCallPurposes.ReplyRecovery), Call(LlmCallPurposes.ReplyRecovery)]), "t2");
        }

        await using var read = new PipelineDbContext(options, new Tenant("x"));
        var reader = new PipelineRunRepository(read, new Tenant("x"));

        (await reader.GetInsightsExtraForTenantAsync("t1")).RepliesAskedAgain.ShouldBe(1);
        (await reader.GetInsightsExtraForTenantAsync("t2")).RepliesAskedAgain.ShouldBe(2);
        (await reader.GetInsightsExtraForTenantAsync("t1", now.AddMinutes(5))).RepliesAskedAgain.ShouldBe(0);
        // The follow-up's tokens still count with the Coding agent's (it is part of that run).
        (await reader.GetCostSummaryForTenantAsync("t1")).TotalTokensIn.ShouldBe(3000);
    }

    private sealed class Tenant(string id) : ITenantContext
    {
        public string TenantId { get; } = id;
        public string? UserId => "u";
        public string? UserName => "u";
        public System.Collections.Generic.IReadOnlyList<string> Roles { get; } = [];
        public bool IsAuthenticated => true;
        public bool IsAdmin => false;
    }
}
