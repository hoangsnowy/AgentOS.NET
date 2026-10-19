// UX phase 2: the Home setup checklist is computed from real state; optional steps never block completion.

using System.Linq;
using AgentOs.Web.Services;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class SetupChecklistTests
{
    [Fact]
    public void Build_FreshWorkspace_NothingDone_KeyFirst()
    {
        var steps = SetupChecklist.Build(new SetupFacts(false, false, false, false, false));
        steps[0].Id.ShouldBe("key");
        steps[0].Action.ShouldBe(SetupAction.ModelKeys);
        SetupChecklist.Progress(steps).ShouldBe((0, 4));   // pairing is optional
        SetupChecklist.IsComplete(steps).ShouldBeFalse();
    }

    [Fact]
    public void IsComplete_AllRequiredDone_EvenWithoutTheOptionalPairing()
    {
        var steps = SetupChecklist.Build(new SetupFacts(true, true, true, true, HasPairedMachine: false));
        steps.Single(s => s.Id == "pair").Optional.ShouldBeTrue();
        SetupChecklist.IsComplete(steps).ShouldBeTrue();
    }

    [Fact]
    public void Build_EachFactDrivesItsStep()
    {
        var steps = SetupChecklist.Build(new SetupFacts(HasModelKey: true, HasRun: false, HasRepository: true, HasBudget: false, HasPairedMachine: true));
        steps.Where(s => s.Done).Select(s => s.Id).ShouldBe(["key", "repo", "pair"]);
        SetupChecklist.Progress(steps).ShouldBe((2, 4));
    }
}
