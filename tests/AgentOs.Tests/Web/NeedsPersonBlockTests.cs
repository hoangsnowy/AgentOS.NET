// 2026-10-24 on the lab: #94's change hit 3 review rounds; NeedsPerson blocked only repos in "Done", but a reviewed
// change sits in "ChangesRequested", so the task stayed Done, never reached the Orchestrator agent's stuck list, and
// "Stopped fixing #94: … It needs you." was logged every minute. NeedsPerson must block every status a session counts
// as Done (SessionRepository.RecomputeSessionStatusForTenantAsync) except Merged.

using AgentOs.Web.Services.Orchestration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class NeedsPersonBlockTests
{
    [Theory]
    [InlineData("Done")]
    [InlineData("Reviewed")]
    [InlineData("ChangesRequested")]
    [InlineData("Accepted")]
    [InlineData("Rejected")]
    public void HoldsAProposedChange_EveryStatusASessionCountsAsDone_IsBlocked(string status)
    {
        ProjectOrchestrator.HoldsAProposedChange(status).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Merged")]
    [InlineData("Blocked")]
    [InlineData("Failed")]
    [InlineData("Running")]
    [InlineData("Pending")]
    [InlineData("Reviewing")]
    [InlineData("Testing")]
    [InlineData(null)]
    public void HoldsAProposedChange_MergedFinishedOrInFlight_IsLeftAlone(string? status)
    {
        ProjectOrchestrator.HoldsAProposedChange(status).ShouldBeFalse();
    }
}

public sealed class ClosedByAgentTests
{
    // VNChess #1 (2026-10-24): an epic with a stuck task was closed with "Posted … after your answer" although nobody
    // had answered.
    [Fact]
    public void ClosedByAgent_SaysTheAgentClosedIt_NeverThanksForAnAnswer()
    {
        var text = AgentOs.Web.Services.Orchestration.ProjectOrchestrator.ClosedByAgent("every M1 item is merged.");

        text.ShouldStartWith("Closing — every M1 item is merged.");
        text.ShouldContain("Reopen it if anything is missing");
        text.ShouldNotContain("your answer");
    }
}
