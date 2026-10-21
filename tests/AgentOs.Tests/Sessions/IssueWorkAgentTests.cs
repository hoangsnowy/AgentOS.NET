// M5 / multi-repo — unit tests for IssueWorkAgent. Uses a mock ILlmClient so no real LLM calls run.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Tests.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public class IssueWorkAgentTests
{
    private static IssueWorkRequest MakeRequest(int issueNumber = 42) => new(
        SessionId: Guid.NewGuid(),
        TenantId: "test-tenant",
        MemberId: "user-1",
        Repos: [new WorkRepo(Guid.NewGuid(), "acme", "my-service", "main")],
        IssueNumber: issueNumber,
        IssueTitle: $"Bug #{issueNumber}: Something is broken",
        IssueBody: "When X happens, Y fails.");

    private static IssueWorkRequest MakeMultiRepoRequest(int issueNumber, params string[] repos) => new(
        SessionId: Guid.NewGuid(),
        TenantId: "test-tenant",
        MemberId: "user-1",
        Repos: repos.Select(r => new WorkRepo(Guid.NewGuid(), "acme", r, "main")).ToList(),
        IssueNumber: issueNumber,
        IssueTitle: "Cross-service ticket",
        IssueBody: "Touches several services.");

    private static IssueWorkAgent MakeAgent(ILlmClient llm) =>
        new(AgentTestHelpers.FactoryReturning(llm),
            AgentTestHelpers.OptionsWith(new AgentsOptions()),
            NullLogger<IssueWorkAgent>.Instance);

    // ── Success path ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SuccessJson_ReturnsBranchAndSummaryPerRepo()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(
               """{"branch":"issue-42-ai-fix","summary":"Replaced null check with guard clause."}"""));

        var result = await MakeAgent(llm).RunAsync(MakeRequest());

        result.Ok.ShouldBeTrue();
        result.Error.ShouldBeNull();
        var repo = result.Repos.ShouldHaveSingleItem();
        repo.Ok.ShouldBeTrue();
        repo.BranchName.ShouldBe("issue-42-ai-fix");
        repo.Summary.ShouldBe("Replaced null check with guard clause.");
    }

    [Fact]
    public async Task RunAsync_JsonEmbeddedInText_ExtractsLastJsonBlock()
    {
        var response = "I explored the repo and found the issue.\n" +
                       "{\"branch\":\"issue-42-ai-fix\",\"summary\":\"Added missing null check.\"}";

        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(response));

        var result = await MakeAgent(llm).RunAsync(MakeRequest());

        result.Ok.ShouldBeTrue();
        result.Repos[0].BranchName.ShouldBe("issue-42-ai-fix");
    }

    [Fact]
    public async Task RunAsync_BracesInsideJsonStringValue_StillParses()
    {
        // The old LastIndexOf('{') boundary hunt grabbed the '{' INSIDE the summary string and
        // reported a successful run as a parse failure. JsonExtractor spans first-{ .. last-}.
        var response = "Done. Summary follows.\n" +
                       "{\"branch\":\"issue-42-ai-fix\",\"summary\":\"Wrapped retries in a { count: 3 } options object.\"}";

        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(response));

        var result = await MakeAgent(llm).RunAsync(MakeRequest());

        result.Ok.ShouldBeTrue();
        result.Repos[0].BranchName.ShouldBe("issue-42-ai-fix");
        result.Repos[0].Summary.ShouldContain("{ count: 3 }");
    }

    [Fact]
    public async Task RunAsync_MarkdownFencedJson_StillParses()
    {
        var response = "```json\n{\"branch\":\"issue-42-ai-fix\",\"summary\":\"Fenced output.\"}\n```";

        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(response));

        var result = await MakeAgent(llm).RunAsync(MakeRequest());

        result.Ok.ShouldBeTrue();
        result.Repos[0].BranchName.ShouldBe("issue-42-ai-fix");
    }

    // ── Multi-repo ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MultipleRepos_RunsEachAndAggregatesOk()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(
               """{"branch":"issue-7-ai-fix","summary":"Done."}"""));

        var result = await MakeAgent(llm).RunAsync(MakeMultiRepoRequest(7, "api", "web", "worker"));

        result.Ok.ShouldBeTrue();
        result.Repos.Count.ShouldBe(3);
        result.Repos.ShouldAllBe(r => r.Ok);
        await llm.Received(3).SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_OneRepoFails_AggregateFails_OthersStillReported()
    {
        // Repos run concurrently now, so call order is non-deterministic — match the stub to the repo
        // in the prompt rather than to call sequence.
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(ci => ci.Arg<LlmRequest>()!.UserPrompt.Contains("acme/web", StringComparison.Ordinal)
               ? AgentTestHelpers.StubResponse("""{"branch":"","summary":"","error":"Build failed in web."}""")
               : AgentTestHelpers.StubResponse("""{"branch":"issue-9-ai-fix","summary":"Fixed api."}"""));

        var result = await MakeAgent(llm).RunAsync(MakeMultiRepoRequest(9, "api", "web"));

        result.Ok.ShouldBeFalse();
        result.Repos.Count.ShouldBe(2);
        // Outcomes stay in input order regardless of completion order.
        result.Repos[0].Repo.ShouldBe("api");
        result.Repos[0].Ok.ShouldBeTrue();
        result.Repos[1].Repo.ShouldBe("web");
        result.Repos[1].Ok.ShouldBeFalse();
        result.Repos[1].Error!.ShouldContain("Build failed");
    }

    private static readonly string[] _orderRepos = ["api", "web", "worker", "infra", "docs"];

    [Fact]
    public async Task RunAsync_ManyRepos_PreservesInputOrderUnderConcurrency()
    {
        // Per-repo stub echoes the repo name into the branch; assert outcomes line up with input order
        // even though the agent runs them in parallel (MaxParallelRepos default > 1).
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(ci =>
           {
               var prompt = ci.Arg<LlmRequest>()!.UserPrompt;
               var repo = _orderRepos.First(r => prompt.Contains($"acme/{r}", StringComparison.Ordinal));
               return AgentTestHelpers.StubResponse($$"""{"branch":"{{repo}}-fix","summary":"ok"}""");
           });

        var result = await MakeAgent(llm).RunAsync(MakeMultiRepoRequest(3, _orderRepos));

        result.Ok.ShouldBeTrue();
        result.Repos.Select(r => r.Repo).ShouldBe(_orderRepos);
        result.Repos.Select(r => r.BranchName).ShouldBe(
            ["api-fix", "web-fix", "worker-fix", "infra-fix", "docs-fix"]);
    }

    [Fact]
    public async Task RunAsync_NoRepos_ReturnsFailure()
    {
        var llm = Substitute.For<ILlmClient>();
        var request = new IssueWorkRequest(
            Guid.NewGuid(), "t", "u", Array.Empty<WorkRepo>(), 1, "T", "B");

        var result = await MakeAgent(llm).RunAsync(request);

        result.Ok.ShouldBeFalse();
        result.Repos.ShouldBeEmpty();
        await llm.DidNotReceive().SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>());
    }

    // ── Failure paths ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_AgentReportsError_ReturnsFailure()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(
               """{"branch":"","summary":"","error":"Build failed: CS0103 — missing dependency."}"""));

        var result = await MakeAgent(llm).RunAsync(MakeRequest());

        result.Ok.ShouldBeFalse();
        result.Repos[0].Error.ShouldNotBeNull();
        result.Repos[0].Error!.ShouldContain("CS0103");
    }

    [Fact]
    public async Task RunAsync_NoJsonInResponse_ReturnsFailure()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse("I could not find the repository."));

        var result = await MakeAgent(llm).RunAsync(MakeRequest());

        result.Ok.ShouldBeFalse();
        result.Repos[0].Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunAsync_EmptyBranchInJson_ReturnsFailure()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(
               """{"branch":"","summary":"Did some work","error":null}"""));

        var result = await MakeAgent(llm).RunAsync(MakeRequest());

        result.Ok.ShouldBeFalse();
        result.Repos[0].BranchName.ShouldBeEmpty();
    }

    // ── Request shape ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_DefaultProvider_IncludesRunnerShellToolNoTimeout()
    {
        LlmRequest? captured = null;
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Do<LlmRequest>(r => captured = r), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(
               """{"branch":"issue-7-ai-fix","summary":"Done."}"""));

        await MakeAgent(llm).RunAsync(MakeRequest(7));

        captured.ShouldNotBeNull();
        captured!.Tools.ShouldNotBeNull();
        captured.Tools!.ShouldContain("runner_shell");
        captured.Timeout.ShouldBeNull();
        captured.Cli.ShouldBeNull(); // server mode never sets a CLI profile
        captured.Autonomy.ShouldBeNull(); // nor asks a runner CLI for hands
    }

    // Ask for changes: whatever branch the reply names, the follow-up result is the pull request's own branch.
    [Fact]
    public async Task RunAsync_FollowUp_OutcomeIsTheExistingBranch()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse("""{"branch":"some-other-branch","summary":"Done."}"""));
        var request = MakeRequest(42) with
        {
            Repos = [new WorkRepo(Guid.NewGuid(), "acme", "my-service", "main", ExistingBranch: "issue-42-ai-fix")],
            ChangeRequest = "Reload before the duplicate check.",
        };

        var result = await MakeAgent(llm).RunAsync(request);

        result.Repos.ShouldHaveSingleItem().BranchName.ShouldBe("issue-42-ai-fix");
    }

    [Fact]
    public async Task RunAsync_RunOnMachine_ForwardsCliProfileToLlmRequest()
    {
        LlmRequest? captured = null;
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Do<LlmRequest>(r => captured = r), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse("""{"branch":"issue-42-ai-fix","summary":"Done."}"""));

        var request = MakeRequest(42) with { ProviderOverride = "RemoteAgent", CliProfile = "codex" };
        await MakeAgent(llm).RunAsync(request);

        captured!.Cli.ShouldBe("codex"); // the runner picks the Codex CLI for this run
        captured.Autonomy.ShouldBe(WorkspaceAutonomy.Scoped); // clone/edit/push in a per-run directory
    }

    [Fact]
    public async Task RunAsync_RunOnMachine_UsesCliPromptWithNoServerToolsAndLongTimeout()
    {
        LlmRequest? captured = null;
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Do<LlmRequest>(r => captured = r), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(
               """{"branch":"issue-42-ai-fix","summary":"Done."}"""));

        // ProviderOverride = "RemoteAgent" routes the whole loop to the member's local CLI.
        var request = MakeRequest(42) with { ProviderOverride = "RemoteAgent" };
        var result = await MakeAgent(llm).RunAsync(request);

        result.Ok.ShouldBeTrue();
        captured.ShouldNotBeNull();
        // The dev-machine CLI uses its OWN tools, so no server runner_shell is exposed…
        (captured!.Tools is null || captured.Tools.Count == 0).ShouldBeTrue();
        // …a generous timeout covers the full clone→build→test→push run…
        captured.Timeout.ShouldNotBeNull();
        captured.Timeout!.Value.ShouldBeGreaterThan(TimeSpan.FromMinutes(5));
        // …and the prompt tells the CLI to clone + push itself rather than call runner_shell.
        captured.SystemPrompt.ShouldContain("clone");
        captured.SystemPrompt.ShouldContain("git push");
        captured.SystemPrompt.ShouldNotContain("runner_shell");
    }

    // ── S1 budget gate ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ServerMode_OverEnforcedBudget_BlocksWithoutCallingLlm()
    {
        var llm = Substitute.For<ILlmClient>();
        var guard = Substitute.For<IBudgetGuard>();
        // cap 10, spent 25 → Exceeded, enforcement ON.
        guard.EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new BudgetStatus(10m, 25m, -15m, 2.5, BudgetState.Exceeded, true));

        var agent = new IssueWorkAgent(
            AgentTestHelpers.FactoryReturning(llm),
            AgentTestHelpers.OptionsWith(new AgentsOptions()),
            NullLogger<IssueWorkAgent>.Instance,
            feed: null,
            budgetGuard: guard);

        var result = await agent.RunAsync(MakeRequest());

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("budget");
        result.Repos.ShouldAllBe(r => !r.Ok);
        // Blocked BEFORE any server tokens are spent.
        await llm.DidNotReceive().SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_OverBudgetButEnforceOff_StillRuns()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse("""{"branch":"issue-42-ai-fix","summary":"Done."}"""));
        var guard = Substitute.For<IBudgetGuard>();
        // Over cap but enforcement OFF → warn-only, never blocks.
        guard.EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new BudgetStatus(10m, 25m, -15m, 2.5, BudgetState.Exceeded, false));

        var agent = new IssueWorkAgent(
            AgentTestHelpers.FactoryReturning(llm),
            AgentTestHelpers.OptionsWith(new AgentsOptions()),
            NullLogger<IssueWorkAgent>.Instance,
            feed: null,
            budgetGuard: guard);

        var result = await agent.RunAsync(MakeRequest());

        result.Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_CliMode_OverEnforcedBudget_IsExemptAndNeverChecksBudget()
    {
        // CLI / RemoteAgent runs execute on the member's paired machine → ZERO server tokens → exempt.
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse("""{"branch":"issue-42-ai-fix","summary":"Done."}"""));
        var guard = Substitute.For<IBudgetGuard>();
        guard.EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new BudgetStatus(10m, 25m, -15m, 2.5, BudgetState.Exceeded, true));

        var agent = new IssueWorkAgent(
            AgentTestHelpers.FactoryReturning(llm),
            AgentTestHelpers.OptionsWith(new AgentsOptions()),
            NullLogger<IssueWorkAgent>.Instance,
            feed: null,
            budgetGuard: guard);

        var result = await agent.RunAsync(MakeRequest() with { ProviderOverride = "RemoteAgent" });

        result.Ok.ShouldBeTrue();
        await guard.DidNotReceive().EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_PromptContainsIssueNumberAndRepo()
    {
        LlmRequest? captured = null;
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Do<LlmRequest>(r => captured = r), Arg.Any<CancellationToken>())
           .Returns(AgentTestHelpers.StubResponse(
               """{"branch":"issue-99-ai-fix","summary":"Done."}"""));

        await MakeAgent(llm).RunAsync(MakeRequest(99));

        captured!.SystemPrompt.ShouldContain("99");
        captured.UserPrompt.ShouldContain("99");
        captured.UserPrompt.ShouldContain("acme/my-service");
    }
}
