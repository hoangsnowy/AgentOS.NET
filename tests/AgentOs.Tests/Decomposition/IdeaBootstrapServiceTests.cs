// Bootstrap (slice 2) — the orchestration: idea → Requirement → deterministic map → decomposer.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Decomposition;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Decomposition;

public sealed class IdeaBootstrapServiceTests
{
    [Fact]
    public async Task GenerateAsync_RunsRequirementThenDecomposer_AndReturnsBoth()
    {
        var spec = DecompositionFixtures.Spec();
        var tickets = new[] { new TicketDraft("Ticket", "body", ["type:feature", "area:core", "p1"], true) };

        var requirement = Substitute.For<IRequirementAgent>();
        requirement.RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>()).Returns(spec);
        var decomposer = Substitute.For<ITicketDecomposerAgent>();
        decomposer.RunAsync(spec, Arg.Any<IReadOnlyList<TicketDraft>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(tickets);

        var svc = new IdeaBootstrapService(requirement, decomposer);
        var preview = await svc.GenerateAsync("build a product catalog");

        preview.Spec.ShouldBe(spec);
        preview.Tickets.ShouldBe(tickets);
        await requirement.Received(1).RunAsync(
            Arg.Is<UserStory>(s => s!.Description == "build a product catalog"), Arg.Any<CancellationToken>());
        // The decomposer is fed the deterministic seed (non-empty) built from the spec.
        await decomposer.Received(1).RunAsync(
            spec, Arg.Is<IReadOnlyList<TicketDraft>>(seed => seed!.Count > 0), "build a product catalog", Arg.Any<CancellationToken>());
        preview.Refined.ShouldBeTrue();
    }

    [Fact]
    public async Task GenerateAsync_DecomposerFellBackToSeed_IsNotRefined()
    {
        var spec = DecompositionFixtures.Spec();
        var requirement = Substitute.For<IRequirementAgent>();
        requirement.RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>()).Returns(spec);
        var decomposer = Substitute.For<ITicketDecomposerAgent>();
        // The real agent returns the very seed list it was given when it can't refine it.
        decomposer.RunAsync(spec, Arg.Any<IReadOnlyList<TicketDraft>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<TicketDraft>>(1));

        var preview = await new IdeaBootstrapService(requirement, decomposer).GenerateAsync("build a product catalog");

        preview.Refined.ShouldBeFalse();
        preview.Tickets.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_BlankIdea_Throws()
    {
        var svc = new IdeaBootstrapService(Substitute.For<IRequirementAgent>(), Substitute.For<ITicketDecomposerAgent>());

        await Should.ThrowAsync<System.ArgumentException>(() => svc.GenerateAsync("   "));
    }
}
