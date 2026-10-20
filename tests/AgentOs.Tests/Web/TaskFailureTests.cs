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
    public void Explain_RealErrors_ClassifiedWithOneAction(string error, string kind, TaskFailureAction action)
    {
        var info = TaskFailure.Explain(error);

        info.ShouldNotBeNull();
        info!.Kind.ShouldBe(kind);
        info.Action.ShouldBe(action);
        info.Headline.ShouldNotBeNullOrWhiteSpace();
        info.ActionLabel.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Explain_NoError_IsNull(string? error) => TaskFailure.Explain(error).ShouldBeNull();

    // Jargon must not leak into the card a member reads.
    [Theory]
    [InlineData("No paired runner connected for member 'abc' in tenant 'default'.")]
    [InlineData("GitHub rejected the PAT for o/r.")]
    [InlineData("exit -1: ")]
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
