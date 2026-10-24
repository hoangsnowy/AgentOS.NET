// The Coding agent writes the pull request description (VNChess #94, 2026-10-24: the ticket asked the PR to list every
// upgraded package with its old and new version, the agent could only return a one-sentence summary, and every fix
// round failed review on it). The body is the agent's description when it gave one, linked to the ticket once.

using AgentOs.Domain.Workspaces;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class PullRequestDescriptionTests
{
    [Fact]
    public void Body_WithDescription_UsesItAndLinksTheTicket()
    {
        var body = PullRequestText.Body(94, "Upgraded packages.", "## Packages\n| Npgsql | 10.0.0 | 10.0.2 |");

        body.ShouldStartWith("## Packages");
        body.ShouldNotContain("Upgraded packages.");
        body.ShouldEndWith("Closes #94");
    }

    [Theory]
    [InlineData("Fixes #94.")]
    [InlineData("closes #94")]
    [InlineData("Resolves #94 — all packages upgraded.")]
    public void Body_DescriptionAlreadyClosesTheTicket_AddsNoSecondLink(string description)
    {
        var body = PullRequestText.Body(94, "s", description);

        body.ShouldBe(description.Trim());
    }

    [Fact]
    public void Body_DescriptionClosingAnotherTicket_StillLinksThisOne()
    {
        PullRequestText.Body(94, "s", "Closes #940 and fixes #9.").ShouldEndWith("Closes #94");
    }

    [Fact]
    public void Body_NoDescription_FallsBackToTheSummary()
    {
        PullRequestText.Body(7, "Added the board.", null).ShouldBe("Added the board.\n\nCloses #7");
        PullRequestText.Body(7, "Added the board.", "   ").ShouldBe("Added the board.\n\nCloses #7");
    }

    [Fact]
    public void Body_HugeDescription_IsCappedUnderGitHubsLimit()
    {
        var body = PullRequestText.Body(1, "s", new string('x', 100_000));

        body.Length.ShouldBeLessThan(65_536);
    }
}
