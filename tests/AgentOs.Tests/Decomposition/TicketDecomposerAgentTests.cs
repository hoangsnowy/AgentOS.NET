// Bootstrap (slice 2) — the LLM right-size pass: parse + taxonomy-filter, ai-gate sync, and the
// seed fallback when the model misbehaves (the property that keeps the bootstrap flow alive).

using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Decomposition;
using AgentOs.Modules.Pipeline.Prompts;
using System.Collections.Generic;
using AgentOs.Tests.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Decomposition;

public sealed class TicketDecomposerAgentTests
{
    private static TicketDecomposerAgent Build(string llmContent)
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(llmContent));
        return new TicketDecomposerAgent(
            AgentTestHelpers.FactoryReturning(llm),
            AgentTestHelpers.OptionsWith(new AgentsOptions()),
            NullLogger<TicketDecomposerAgent>.Instance);
    }

    [Fact]
    public async Task RunAsync_ParsesTickets_AndDropsNonTaxonomyLabels()
    {
        var json = """
            { "tickets": [
              { "title": "Add SKU uniqueness check", "body": "do it",
                "labels": ["type:feature","area:data","p1","ai:ready","bogus:label"], "aiReady": true }
            ] }
            """;

        var result = await Build(json).RunAsync(DecompositionFixtures.Spec(), DecompositionFixtures.Seed());

        result.Count.ShouldBe(1);
        result[0].Title.ShouldBe("Add SKU uniqueness check");
        result[0].Labels.ShouldNotContain("bogus:label");
        result[0].Labels.ShouldContain(StandardLabels.AiReady);
        result[0].AiReady.ShouldBeTrue();
    }

    // dependsOn points at the model's own list; a dropped blank ticket shifts indices, so they are re-mapped, and
    // self or dangling references disappear.
    [Fact]
    public async Task RunAsync_DependsOn_RemappedAcrossDroppedTickets()
    {
        var json = """
            { "tickets": [
              { "title": "Add the module", "body": "", "labels": ["type:feature","area:data","p0"], "aiReady": true },
              { "title": "", "body": "blank, dropped" },
              { "title": "Add the API", "body": "", "labels": ["type:feature","area:api","p1"], "aiReady": true, "dependsOn": [0, 1, 2, 9] },
              { "title": "Add the page", "body": "", "labels": ["type:feature","area:ui","p1"], "aiReady": true, "dependsOn": [2] }
            ] }
            """;

        var result = await Build(json).RunAsync(DecompositionFixtures.Spec(), DecompositionFixtures.Seed());

        result.Count.ShouldBe(3);
        result[0].DependsOn.ShouldBeEmpty();
        result[1].DependsOn.ShouldBe([0]);   // the blank, itself and the out-of-range index are gone
        result[2].DependsOn.ShouldBe([1]);   // "the API" moved from index 2 to 1
    }

    [Fact]
    public async Task RunAsync_AiReadyFalse_ForcesNeedsHumanAndDropsAiReady()
    {
        var json = """
            { "tickets": [
              { "title": "Coordinate the epic", "body": "", "labels": ["type:feature","area:core","p1","ai:ready"], "aiReady": false }
            ] }
            """;

        var result = await Build(json).RunAsync(DecompositionFixtures.Spec(), DecompositionFixtures.Seed());

        result[0].AiReady.ShouldBeFalse();
        result[0].Labels.ShouldContain(StandardLabels.NeedsHuman);
        result[0].Labels.ShouldNotContain(StandardLabels.AiReady);
    }

    [Fact]
    public async Task RunAsync_UnparseableResponse_FallsBackToSeed()
    {
        var seed = DecompositionFixtures.Seed();

        var result = await Build("not json at all").RunAsync(DecompositionFixtures.Spec(), seed);

        result.ShouldBeSameAs(seed);
    }

    [Fact]
    public async Task RunAsync_EmptyTicketArray_FallsBackToSeed()
    {
        var seed = DecompositionFixtures.Seed();

        var result = await Build("""{ "tickets": [] }""").RunAsync(DecompositionFixtures.Spec(), seed);

        result.ShouldBeSameAs(seed);
    }

    [Fact]
    public async Task RunAsync_DependsOnIssues_KeepsOnlyNumbersTheIdeaNames()
    {
        var json = """
            { "tickets": [
              { "title": "Add the import screen", "body": "", "labels": ["type:feature","area:ui","p1"], "aiReady": true, "dependsOnIssues": [12, 99, 12] },
              { "title": "Add search", "body": "", "labels": ["type:feature","area:ui","p1"], "aiReady": true }
            ] }
            """;

        var result = await Build(json).RunAsync(DecompositionFixtures.Spec(), DecompositionFixtures.Seed(),
            "Build an import screen on top of #12 (the endpoint) and search in #14's page; issue#5 is not a reference.");

        result[0].DependsOnIssues.ShouldBe([12]);   // #99 is not in the idea — never a dependency
        result[1].DependsOnIssues.ShouldBeEmpty();
    }

    [Fact]
    public void IssueNumbersIn_FindsHashReferencesOnly()
    {
        TicketDecomposerAgent.IssueNumbersIn("after #12, #13 and (#16); not issue#5 or &#39; or #0").ShouldBe(new HashSet<int> { 12, 13, 16 }, ignoreOrder: true);
        TicketDecomposerAgent.IssueNumbersIn(null).ShouldBeEmpty();
    }

    [Fact]
    public void RenderUser_WithIdea_FencesThePersonsWords()
    {
        var prompt = DecomposerPrompt.RenderUser(DecompositionFixtures.Spec(), DecompositionFixtures.Seed(), "Depends on #12 please");
        prompt.ShouldContain("<idea>");
        prompt.ShouldContain("Depends on #12 please");
        DecomposerPrompt.RenderUser(DecompositionFixtures.Spec(), DecompositionFixtures.Seed()).ShouldNotContain("<idea>");
    }
}
