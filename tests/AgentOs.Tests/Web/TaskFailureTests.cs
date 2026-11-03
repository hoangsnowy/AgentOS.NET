// Failed Board tasks explained for non-developers. The inputs are real error texts seen on the lab.

using AgentOs.Web.Services;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class TaskFailureTests
{
    [Theory]
    // The first live run on VNChessAcademy #7, prompt-mode runner.
    [InlineData("I couldn't start: this session needs approval to clone, and it runs non-interactively, so nobody could grant it.", "needs-permission", TaskFailureAction.TryAgain)]
    [InlineData("No paired runner connected for member 'abc' in tenant 'default'.", "computer-offline", TaskFailureAction.ConnectComputer)]
    [InlineData("No remote dev runner connected for you. Register a runner (POST /runners), start the AgentOS.NET remote agent…", "computer-offline", TaskFailureAction.ConnectComputer)]
    [InlineData("No Claude API key configured for your workspace. Open Settings → AI → Model keys and add your key.", "no-ai-account", TaskFailureAction.ConnectAiAccount)]
    [InlineData("LLM client init failed: provider 'X' is not registered", "no-ai-account", TaskFailureAction.ConnectAiAccount)]
    [InlineData("GitHub rejected the PAT for o/r. Check the token's 'repo' scope.", "github-access", TaskFailureAction.ReconnectGitHub)]
    [InlineData("Blocked — monthly LLM budget exceeded ($21 of $20).", "spending-limit", TaskFailureAction.RaiseSpendingLimit)]
    [InlineData("Remote agent timed out after 1200s.", "timeout", TaskFailureAction.TryAgain)]
    [InlineData("Stopped — you pressed Stop. Nothing was pushed for the unfinished parts; Try again to restart.", "stopped", TaskFailureAction.TryAgain)]
    [InlineData("src/A.cs(3,1): error CS1002: ; expected", "build", TaskFailureAction.TryAgain)]
    [InlineData("exit -1: ", "unknown", TaskFailureAction.TryAgain)]
    // The member's computer answered with its Claude plan's limit (claude -p, 2026-10).
    [InlineData("LLM call failed: You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)", "plan-limit", TaskFailureAction.TryAgain)]
    public void Explain_RealErrors_ClassifiedWithOneAction(string error, string kind, TaskFailureAction action)
    {
        var info = TaskFailure.Explain(error);

        info.ShouldNotBeNull();
        info!.Kind.ShouldBe(kind);
        info.Action.ShouldBe(action);
        info.Headline.ShouldNotBeNullOrWhiteSpace();
        info.ActionLabel.ShouldNotBeNullOrWhiteSpace();
    }

    // Autopilot used up its automatic fixes (lab, 2026-11): these showed the Unknown card — "The AI couldn't finish this
    // task … Trying again often works." with Try again — although nothing broke and a decision was missing.
    [Theory]
    // The rules (AutopilotPlanner).
    [InlineData("Still conflicts with its base after automatic fixes — needs a person.", "needs-decision-conflicts")]
    [InlineData("Checks still fail after 3 automatic fixes — needs a person.", "needs-decision-checks")]
    [InlineData("Code review still asks for changes after 3 automatic fixes — needs a person.", "needs-decision-review")]
    [InlineData("Acceptance testing still fails after 3 automatic fixes — needs a person.", "needs-decision-acceptance")]
    [InlineData("Design review still asks for changes after 3 automatic fixes — needs a person.", "needs-decision-design")]
    // A ticket's MAF workflow (TicketFlow.SendBack / the Design gate).
    [InlineData("Conflicts with its base after 3 automatic fixes — needs a person.", "needs-decision-conflicts")]
    [InlineData("Checks failed after 3 automatic fixes — needs a person.", "needs-decision-checks")]
    [InlineData("Code review asked for changes after 3 automatic fixes — needs a person.", "needs-decision-review")]
    [InlineData("Acceptance testing failed after 3 automatic fixes — needs a person.", "needs-decision-acceptance")]
    [InlineData("Design review asked for changes after 3 automatic fixes — needs a person.", "needs-decision-design")]
    [InlineData("The design needs a person's eye — approve it or ask for changes on Board › Tasks. The colours are off.", "needs-decision-design-eye")]
    [InlineData("Something else after 3 automatic fixes — needs a person.", "needs-decision")]
    public void Explain_NeedsAPerson_AsksForTheMembersDecision(string error, string kind)
    {
        var info = TaskFailure.Explain(error)!;

        info.Kind.ShouldBe(kind);
        info.Action.ShouldBe(TaskFailureAction.AskForChanges);
        info.Headline.ShouldBe("The AI needs your decision");
        info.ActionLabel.ShouldBe("Tell the AI what to do");
        info.Advice.ShouldContain("answer on the ticket and Autopilot picks it up");
        info.Advice.ShouldNotContain("Trying again");
    }

    // The needs-a-person rules must not take over other failures (first match wins).
    [Theory]
    [InlineData("Test run failed: 2 of 10", "tests")]
    [InlineData("Build FAILED.", "build")]
    [InlineData("Remote agent timed out after 1200s.", "timeout")]
    [InlineData("Stopped by you", "stopped")]
    public void Explain_OtherFailures_KeepTheirCard(string error, string kind) =>
        TaskFailure.Explain(error)!.Kind.ShouldBe(kind);

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Explain_NoError_IsNull(string? error) => TaskFailure.Explain(error).ShouldBeNull();

    // Jargon must not leak into the card a member reads.
    [Theory]
    [InlineData("No paired runner connected for member 'abc' in tenant 'default'.")]
    [InlineData("GitHub rejected the PAT for o/r.")]
    [InlineData("exit -1: ")]
    [InlineData("Code review still asks for changes after 3 automatic fixes — needs a person.")]
    [InlineData("The design needs a person's eye — approve it or ask for changes on Board › Tasks.")]
    public void Explain_Copy_HasNoJargon(string error)
    {
        var info = TaskFailure.Explain(error)!;
        var copy = info.Headline + " " + info.Advice + " " + info.ActionLabel;
        foreach (var word in new[] { "runner", "PAT", "tenant", "exit", "LLM", "CLI", "session" })
        {
            copy.ShouldNotContain(word, Case.Insensitive);
        }
    }
}
