// Insights shows one row per agent role: the 5-agent pipeline files calls as "CodingAgent", the Board's agents as
// "Coding" — on the lab both showed as two separate "Coding" rows.

using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Web.Components.Pages;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class InsightsByRoleTests
{
    [Fact]
    public void ByRole_MergesTheSameRole_AndSumsEverything()
    {
        CostBucket[] buckets =
        [
            new("CodingAgent", 0.01m, 1000, 200, 3, 100, 10),
            new("Coding", 0m, 5000, 900, 5, 4000, 300),
            new("Reviewer", 0m, 700, 50, 1),
        ];

        var rows = InsightsApp.ByRole(buckets).ToDictionary(b => b.Key);

        rows.Count.ShouldBe(2);
        rows["Coding"].TokensIn.ShouldBe(6000);
        rows["Coding"].Calls.ShouldBe(8);
        rows["Coding"].CacheReadTokens.ShouldBe(4100);
        rows["Coding"].CostUsd.ShouldBe(0.01m);
    }

    [Fact]
    public void LatencyByRole_IsCallWeighted()
    {
        AgentLatencyBucket[] buckets = [new("CodingAgent", 1000, 1), new("Coding", 4000, 3)];

        InsightsApp.LatencyByRole(buckets).ShouldHaveSingleItem().AvgLatencyMs.ShouldBe(3250);
    }
}
