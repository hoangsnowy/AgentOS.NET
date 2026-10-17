// Per-agent token accounting for a run: how many tokens each agent used, in the order the agents ran,
// with offline (canned) answers flagged as estimates so they are never presented as provider-reported usage.

using System;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Pipeline.GraphExecution;
using AgentOs.Modules.Pipeline.Metrics;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.GraphExecution;

public sealed class UsageByAgentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static RunMetric M(string agent, int sec, int tokIn, int tokOut, decimal cost, string provider = "Claude")
        => new("run", "KC1", 0, agent, "m", provider, tokIn, tokOut, 10, cost, true, null, T0.AddSeconds(sec));

    [Fact]
    public void UsageByAgent_SumsPerAgent_InFirstRunOrder()
    {
        var usage = GraphExecutor.UsageByAgent([
            M("CodingAgent", 2, 1000, 400, 0.02m),
            M("RequirementAgent", 1, 500, 200, 0.01m),
            M("CodingAgent", 5, 900, 300, 0.015m),   // second QA iteration
            M("QaAgent", 3, 300, 50, 0.001m),
        ]);

        usage.Count.ShouldBe(3);
        usage[0].Agent.ShouldBe("RequirementAgent");
        usage[1].Agent.ShouldBe("CodingAgent");
        usage[1].Calls.ShouldBe(2);
        usage[1].TokensIn.ShouldBe(1900);
        usage[1].TokensOut.ShouldBe(700);
        usage[1].TotalTokens.ShouldBe(2600);
        usage[1].CostUsd.ShouldBe(0.035m);
        usage[1].Estimated.ShouldBeFalse();
    }

    [Fact]
    public void UsageByAgent_FlagsOfflineAnswersAsEstimates()
    {
        var usage = GraphExecutor.UsageByAgent([
            M("TestingAgent", 1, 700, 900, 0m, LlmProviders.Offline),
            M("QaAgent", 2, 300, 40, 0.001m),
        ]);

        usage[0].Estimated.ShouldBeTrue();
        usage[1].Estimated.ShouldBeFalse();
    }

    [Fact]
    public void UsageByAgent_NoCalls_IsEmpty()
        => GraphExecutor.UsageByAgent([]).ShouldBeEmpty();
}
