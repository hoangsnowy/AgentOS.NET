// VNChess #95 / PR #97 (2026-10-24): a fix round failed (the push was blocked), BoardTaskService marked the repo
// Failed and dropped the pull request link, so Autopilot lost the open PR and would start the ticket over on a new
// branch. A failed follow-up now leaves the change where it stood.

using AgentOs.Modules.Pipeline.Prompts;
using AgentOs.Web.Services.BoardTasks;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class FailedFollowUpTests
{
    private const string Pr = "https://github.com/o/r/pull/97";

    [Theory]
    [InlineData("Done")]
    [InlineData("Reviewed")]
    [InlineData("ChangesRequested")]
    [InlineData("Accepted")]
    [InlineData("Rejected")]
    [InlineData("Blocked")]
    public void FailedFollowUp_OnAnOpenChange_KeepsIt(string status)
    {
        BoardTaskService.FailedFollowUpKeepsItsChange(isFollowUp: true, status, Pr).ShouldBeTrue();
    }

    [Fact]
    public void FailedFreshStart_IsAFailure()
    {
        BoardTaskService.FailedFollowUpKeepsItsChange(isFollowUp: false, "Done", Pr).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Done", null)]
    [InlineData("Merged", Pr)]
    [InlineData("Failed", Pr)]
    [InlineData("Running", Pr)]
    public void FailedFollowUp_WithNoOpenChange_IsAFailure(string status, string? prUrl)
    {
        BoardTaskService.FailedFollowUpKeepsItsChange(isFollowUp: true, status, prUrl).ShouldBeFalse();
    }

    [Fact]
    public void FollowUpPrompt_NamesTheOnlyPushThatIsAllowed_AndTheDescriptionField()
    {
        // The scoped allowlist permits "git push origin <branch>" only; the agent tried other forms and gh pr edit.
        var repo = new AgentOs.Domain.Sessions.WorkRepo(System.Guid.NewGuid(), "o", "r", "main", ExistingBranch: "issue-95-ai-fix");
        var req = new AgentOs.Domain.Sessions.IssueWorkRequest(System.Guid.NewGuid(), "t", "m", [repo], 95, "Upgrade web dependencies",
            "body", ChangeRequest: "Add the package table.");
        var text = IssueWorkPrompt.User(req, repo);

        text.ShouldContain("git push origin issue-95-ai-fix");
        text.ShouldContain("\"description\"");
        text.ShouldContain("gh pr edit");
    }
}
