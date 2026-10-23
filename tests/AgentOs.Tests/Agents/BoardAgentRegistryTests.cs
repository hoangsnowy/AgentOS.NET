// One agent registry: every Board agent (Coding / Reviewer / Testing (tickets), the Autopilot lead, Retrospective,
// Discovery) is configured from Settings → Agent profiles and Settings → Prompts like the pipeline agents, files its
// calls under its role key (what Insights groups by), reads the project's practices and passes the budget gate —
// decided from the EFFECTIVE provider, so a forced server provider never slips past it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Memory;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Decomposition;
using AgentOs.Modules.Pipeline.Prompts;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Agents;

public sealed class BoardAgentRegistryTests
{
    private const string Tenant = "acme";
    private static readonly string[] BoardRoles =
        [AgentRoles.Discovery, AgentRoles.IssueWork, AgentRoles.Reviewer, AgentRoles.Acceptance, AgentRoles.AutopilotOrchestrator, AgentRoles.Retrospective];

    private static readonly IReadOnlyList<string> ShopRepo = ["acme/shop"];

    private sealed record Call(string Provider, LlmRequest Request);

    // A factory whose clients report `effective` as their provider (what a force-provider would return) and record
    // every request with the provider name the agent asked for.
    private static (ILlmClientFactory Factory, List<Call> Calls) Recording(string reply, string? effective = null)
    {
        var calls = new List<Call>();
        var factory = Substitute.For<ILlmClientFactory>();
        factory.Create(Arg.Any<string>()).Returns(ci =>
        {
            var asked = ci.Arg<string>();
            var client = Substitute.For<ILlmClient>();
            client.Provider.Returns(effective ?? asked);
            client.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(x =>
            {
                calls.Add(new Call(asked, x.Arg<LlmRequest>()));
                return AgentTestHelpers.StubResponse(reply);
            });
            return client;
        });
        return (factory, calls);
    }

    private static SettingsService Settings() => new(
        new SettingsCatalog([new LlmSettings(), new AgentSettings()]), new InMemoryAppConfigStore(),
        new ConfigurationBuilder().Build(), new NullSettingsAuditSink(), NullLogger<SettingsService>.Instance);

    private static IBudgetGuard OverBudget()
    {
        var guard = Substitute.For<IBudgetGuard>();
        guard.EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new BudgetStatus(10m, 25m, -15m, 2.5, BudgetState.Exceeded, true));
        return guard;
    }

    private static AcceptanceRequest Change() => new(Guid.NewGuid(), Tenant, "u1",
        new WorkRepo(Guid.NewGuid(), "acme", "shop", "main", "issue-7-ai-fix"), 7, "Checkout", "Pay by card.");

    private static OrchestratorBrief Brief() => new(Guid.NewGuid(), Tenant, "u1", "Shop", "Build", "Tickets:\n#7 [open]",
        [new OrchestratorMove("M1", "start", 7, "start #7")], [], 1, []);

    private static RetrospectiveInput Lessons() => new(Tenant, "u1", "acme/shop", "#7 Checkout",
        [new RetrospectiveItem("L1", "Run the E2E specs with the AppHost running.", false)], []);

    private const string Verdict = """{"verdict":"pass","criteria":[],"summary":"ok"}""";
    private const string Approve = """{"verdict":"approve","findings":[],"summary":"ok"}""";
    private const string Decision = """{"reasoning":"go","moves":["M1"],"retries":[],"questions":[],"closes":[]}""";
    private const string Practices = """{"practices":["Run the E2E specs with the AppHost running."],"absorbed":["L1"],"summary":"one"}""";

    // ── The registry ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_EveryRoleHasAProfileRow_AndBoardRowsAreSeparateFromThePipeline()
    {
        AgentSettings.Roles.Select(r => r.Role).ShouldBe(AgentRoles.All.Select(r => r.Key));
        AgentRoles.All.Where(r => r.Group == AgentRoleGroup.Board).Select(r => r.Key).ShouldBe(BoardRoles, ignoreOrder: true);
        AgentRoles.All.Where(r => r.Group == AgentRoleGroup.WorkflowPipeline).ShouldAllBe(r => r.Label.EndsWith("(Workflow pipeline)", StringComparison.Ordinal));
        AgentRoles.All.Select(r => r.Label).ShouldBeUnique();
    }

    [Theory]
    [InlineData(AgentRoles.Reviewer, 32768)]
    [InlineData(AgentRoles.Acceptance, 32768)]
    [InlineData(AgentRoles.AutopilotOrchestrator, 4096)]
    [InlineData(AgentRoles.Retrospective, 4096)]
    public void Seeds_KeepTodaysBehaviour_OnTheMembersComputer(string role, int maxTokens)
    {
        var seed = new AgentsOptions().For(role).ShouldNotBeNull();
        seed.Provider.ShouldBe("RemoteAgent");
        seed.Model.ShouldBeEmpty();   // the computer's own model — never a pinned, older id
        seed.MaxTokens.ShouldBe(maxTokens);
    }

    [Theory]
    [InlineData("CodingAgent", AgentRoles.Coding, "Coding (Workflow pipeline)")]
    [InlineData("Coding", AgentRoles.Coding, "Coding (Workflow pipeline)")]
    [InlineData("IssueWork", AgentRoles.IssueWork, "Coding (tickets)")]
    [InlineData("Acceptance", AgentRoles.Acceptance, "Testing (tickets)")]
    [InlineData("Decomposer", AgentRoles.Discovery, "Discovery")]
    [InlineData("AutopilotOrchestrator", AgentRoles.AutopilotOrchestrator, "Autopilot lead")]
    [InlineData("QaAgent", AgentRoles.Qa, "QA (Workflow pipeline)")]
    [InlineData("SomethingNew", "SomethingNew", "SomethingNew")]
    public void Insights_NamesMatchAgentProfiles(string stored, string key, string label)
    {
        AgentRoles.Canonical(stored).ShouldBe(key);
        AgentRoles.Label(stored).ShouldBe(label);
    }

    [Fact]
    public void Prompts_EveryBoardRoleIsTunable_AndKeysAreUnique()
    {
        var keys = PromptCatalog.All.Select(e => e.Key).ToList();
        keys.ShouldBeUnique();
        foreach (var role in BoardRoles) { keys.ShouldContain(role); }
        PromptCatalog.All.Where(e => e.Group == AgentRoleGroup.Board && !e.Key.StartsWith(AgentRoles.IssueWork, StringComparison.Ordinal))
            .ShouldAllBe(e => e.DefaultPrompt.StartsWith("You are the ", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptTemplate_FillsInOnePass_AndLeavesJsonAndUnknownWordsAlone()
    {
        var filled = PromptTemplate.Fill("""clone {owner}/{repo} {unknown} {"verdict":"pass"}""",
            new Dictionary<string, string> { ["owner"] = "{repo}", ["repo"] = "shop" });

        filled.ShouldBe("""clone {repo}/shop {unknown} {"verdict":"pass"}""");
    }

    // ── Profiles: provider + model per call ─────────────────────────────────────────────────────

    [Fact]
    public async Task BoardAgents_UseTheirProfile_AndFileCallsUnderTheirRole()
    {
        var settings = Settings();
        foreach (var role in new[] { AgentRoles.Reviewer, AgentRoles.Acceptance, AgentRoles.AutopilotOrchestrator, AgentRoles.Retrospective })
        {
            (await settings.SetAsync(AgentSettings.Key(role, "Model"), "claude-opus-5-5", SettingScope.Tenant, Tenant, "admin")).Ok.ShouldBeTrue();
        }
        var profiles = new SettingsAgentProfiles(settings);
        var (review, reviewCalls) = Recording(Approve);
        var (test, testCalls) = Recording(Verdict);
        var (lead, leadCalls) = Recording(Decision);
        var (retro, retroCalls) = Recording(Practices);

        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            (await new ReviewAgent(review, NullLogger<ReviewAgent>.Instance, profiles: profiles).RunAsync(Change())).Ran.ShouldBeTrue();
            (await new AcceptanceAgent(test, NullLogger<AcceptanceAgent>.Instance, profiles: profiles).RunAsync(Change())).Ran.ShouldBeTrue();
            (await new AutopilotOrchestratorAgent(lead, NullLogger<AutopilotOrchestratorAgent>.Instance, profiles: profiles).DecideAsync(Brief())).Ran.ShouldBeTrue();
            (await new RetrospectiveAgent(retro, NullLogger<RetrospectiveAgent>.Instance, profiles: profiles).RunAsync(Lessons())).Ran.ShouldBeTrue();
        }

        foreach (var (calls, role) in new[] { (reviewCalls, AgentRoles.Reviewer), (testCalls, AgentRoles.Acceptance), (leadCalls, AgentRoles.AutopilotOrchestrator), (retroCalls, AgentRoles.Retrospective) })
        {
            var call = calls.ShouldHaveSingleItem();
            call.Provider.ShouldBe("RemoteAgent");                 // the seed provider, still on the member's computer
            call.Request.Model.ShouldBe("claude-opus-5-5");        // the tenant's pick, not a hard-coded model
            call.Request.AgentRole.ShouldBe(role);                 // Insights files it under the Agent profiles row
        }
    }

    [Fact]
    public async Task BoardAgent_ProviderChangedInSettings_IsTheOneAskedFor()
    {
        var settings = Settings();
        (await settings.SetAsync(AgentSettings.Key(AgentRoles.Reviewer, "Provider"), "Claude", SettingScope.Tenant, Tenant, "admin")).Ok.ShouldBeTrue();
        var (factory, calls) = Recording(Approve);

        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            await new ReviewAgent(factory, NullLogger<ReviewAgent>.Instance, profiles: new SettingsAgentProfiles(settings)).RunAsync(Change());
        }

        calls.ShouldHaveSingleItem().Provider.ShouldBe("Claude");
    }

    // ── Budget gate: decided from the effective provider ────────────────────────────────────────

    [Fact]
    public async Task BoardAgents_OnAForcedServerProvider_PassTheBudgetGate()
    {
        // The profiles say RemoteAgent, but Settings forces a server provider: the gate must apply.
        var guard = OverBudget();
        var (factory, calls) = Recording(Approve, effective: "Claude");

        var review = await new ReviewAgent(factory, NullLogger<ReviewAgent>.Instance, budgetGuard: guard).RunAsync(Change());
        var test = await new AcceptanceAgent(factory, NullLogger<AcceptanceAgent>.Instance, budgetGuard: guard).RunAsync(Change());
        var lead = await new AutopilotOrchestratorAgent(factory, NullLogger<AutopilotOrchestratorAgent>.Instance, budgetGuard: guard).DecideAsync(Brief());
        var retro = await new RetrospectiveAgent(factory, NullLogger<RetrospectiveAgent>.Instance, budgetGuard: guard).RunAsync(Lessons());

        review.Ran.ShouldBeFalse();
        review.Error.ShouldBe(AgentBudgetGate.BlockedMessage);
        test.Ran.ShouldBeFalse();
        lead.Ran.ShouldBeFalse();
        retro.Ran.ShouldBeFalse();
        calls.ShouldBeEmpty();   // blocked before any server token is spent
    }

    [Fact]
    public async Task BoardAgents_OnTheMembersComputer_AreExempt_AndNeverAskTheBudget()
    {
        var guard = OverBudget();
        var (factory, calls) = Recording(Verdict);

        (await new AcceptanceAgent(factory, NullLogger<AcceptanceAgent>.Instance, budgetGuard: guard).RunAsync(Change())).Ran.ShouldBeTrue();

        calls.ShouldHaveSingleItem();
        await guard.DidNotReceive().EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IssueWork_ForcedServerProvider_IsGated_EvenWhenTheRunAskedForTheMembersComputer()
    {
        var guard = OverBudget();
        var (factory, calls) = Recording("""{"branch":"issue-7-ai-fix","summary":"done"}""", effective: "Claude");
        var agent = new IssueWorkAgent(factory, Options.Create(new AgentsOptions()), NullLogger<IssueWorkAgent>.Instance, budgetGuard: guard);

        var result = await agent.RunAsync(new IssueWorkRequest(Guid.NewGuid(), Tenant, "u1",
            [new WorkRepo(Guid.NewGuid(), "acme", "shop", "main")], 7, "Checkout", "Pay by card.") { ProviderOverride = "RemoteAgent" });

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("budget");
        calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task IssueWork_FilesItsCallsUnderItsRole()
    {
        var (factory, calls) = Recording("""{"branch":"issue-7-ai-fix","summary":"done"}""");
        var agent = new IssueWorkAgent(factory, Options.Create(new AgentsOptions()), NullLogger<IssueWorkAgent>.Instance);

        await agent.RunAsync(new IssueWorkRequest(Guid.NewGuid(), Tenant, "u1",
            [new WorkRepo(Guid.NewGuid(), "acme", "shop", "main")], 7, "Checkout", "Pay by card."));

        calls.ShouldHaveSingleItem().Request.AgentRole.ShouldBe(AgentRoles.IssueWork);
    }

    // ── Prompts: Settings → Prompts overrides reach the Board agents ────────────────────────────

    [Fact]
    public async Task BoardAgents_UseTheTenantsPromptOverride_FilledPerRun_ThenTheFixedRules()
    {
        var prompts = Substitute.For<IPromptOverrides>();
        prompts.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci => ci.ArgAt<string>(1));
        prompts.ResolveAsync(AgentRoles.Reviewer, Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns("You are the Reviewer Agent of ACME. Check out {branch} of {owner}/{repo}.");
        prompts.ResolveRulesAsync(Arg.Any<CancellationToken>()).Returns("Never touch /infra.");
        var (factory, calls) = Recording(Approve);

        await new ReviewAgent(factory, NullLogger<ReviewAgent>.Instance, prompts: prompts).RunAsync(Change());

        var system = calls.ShouldHaveSingleItem().Request.SystemPrompt;
        system.ShouldStartWith("You are the Reviewer Agent of ACME. Check out issue-7-ai-fix of acme/shop.");
        system.ShouldContain(UntrustedText.SystemRule);            // the security rule is never overridable
        system.ShouldContain("<workspace_rules>\nNever touch /infra.");
    }

    [Theory]
    [InlineData(null, AgentRoles.IssueWork)]                     // the member's computer (Board default)
    [InlineData("Claude", "IssueWork.Server")]                    // the server runner_shell loop
    public async Task IssueWork_ResolvesThePromptOfItsMode(string? effective, string key)
    {
        var prompts = Substitute.For<IPromptOverrides>();
        prompts.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("CUSTOM {owner}/{repo} -> {branch}");
        var (factory, calls) = Recording("""{"branch":"issue-7-ai-fix","summary":"done"}""", effective ?? "RemoteAgent");
        var agent = new IssueWorkAgent(factory, Options.Create(new AgentsOptions()), NullLogger<IssueWorkAgent>.Instance, prompts: prompts);

        await agent.RunAsync(new IssueWorkRequest(Guid.NewGuid(), Tenant, "u1",
            [new WorkRepo(Guid.NewGuid(), "acme", "shop", "main")], 7, "Checkout", "Pay by card."));

        await prompts.Received().ResolveAsync(key, Arg.Any<string>(), Arg.Any<CancellationToken>());
        calls.ShouldHaveSingleItem().Request.SystemPrompt.ShouldStartWith("CUSTOM acme/shop -> issue-7-ai-fix");
    }

    // ── Practices ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Testing_And_AutopilotLead_ReadTheProjectsPractices()
    {
        var memory = new PracticesMemory(Tenant, "acme/shop", "Run the E2E specs with the AppHost running.");
        var boardRepos = Substitute.For<IBoardRepos>();
        boardRepos.RepoKeysAsync(Tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ShopRepo);
        var (test, testCalls) = Recording(Verdict);
        var (lead, leadCalls) = Recording(Decision);

        await new AcceptanceAgent(test, NullLogger<AcceptanceAgent>.Instance, memory: memory).RunAsync(Change());
        await new AutopilotOrchestratorAgent(lead, NullLogger<AutopilotOrchestratorAgent>.Instance, memory: memory, boardRepos: boardRepos).DecideAsync(Brief());

        testCalls.ShouldHaveSingleItem().Request.UserPrompt.ShouldContain("Run the E2E specs with the AppHost running.");
        leadCalls.ShouldHaveSingleItem().Request.UserPrompt.ShouldContain("Run the E2E specs with the AppHost running.");
    }

    [Fact]
    public async Task Practices_AreStrictlyPerTenant()
    {
        var memory = new PracticesMemory("other-tenant", "acme/shop", "A practice of someone else.");
        var (test, calls) = Recording(Verdict);

        await new AcceptanceAgent(test, NullLogger<AcceptanceAgent>.Instance, memory: memory).RunAsync(Change());

        calls.ShouldHaveSingleItem().Request.UserPrompt.ShouldNotContain("someone else");
    }

    // ── Discovery ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Discovery_TheRequirementStepAndTheSplit_UseTheDiscoveryProfile_AndRole()
    {
        var settings = Settings();
        (await settings.SetAsync(AgentSettings.Key(AgentRoles.Discovery, "Model"), "claude-haiku-5-5", SettingScope.Tenant, Tenant, "admin")).Ok.ShouldBeTrue();
        var profiles = new SettingsAgentProfiles(settings);
        var offline = new OfflineLlmClient();
        var calls = new List<LlmRequest>();
        var factory = Substitute.For<ILlmClientFactory>();
        factory.Create(Arg.Any<string>()).Returns(_ =>
        {
            var client = Substitute.For<ILlmClient>();
            client.Provider.Returns("RemoteAgent");
            client.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(x =>
            {
                calls.Add(x.Arg<LlmRequest>());
                return offline.SendAsync(x.Arg<LlmRequest>(), x.Arg<CancellationToken>());
            });
            return client;
        });
        var options = Options.Create(new AgentsOptions());
        var requirement = new RequirementAgent(factory, options, AgentTestHelpers.Validator, AgentTestHelpers.NewCollector(),
            NullLogger<RequirementAgent>.Instance, profiles: profiles);
        var decomposer = new TicketDecomposerAgent(factory, options, NullLogger<TicketDecomposerAgent>.Instance, profiles);

        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            await new IdeaBootstrapService(requirement, decomposer, options).GenerateAsync("A shop with card payments");
            // The same Requirement agent outside Discovery keeps its own (Workflow pipeline) profile.
            await requirement.RunAsync(new UserStory("A shop with card payments"));
        }

        calls.Count.ShouldBe(3);
        calls[0].AgentRole.ShouldBe(AgentRoles.Discovery);
        calls[0].Model.ShouldBe("claude-haiku-5-5");
        calls[0].SystemPrompt.ShouldStartWith("You are the Requirement Agent");   // the spec contract stays the Requirement agent's
        calls[1].AgentRole.ShouldBe(AgentRoles.Discovery);
        calls[1].Model.ShouldBe("claude-haiku-5-5");
        calls[2].AgentRole.ShouldBe(AgentRoles.Requirement);
        calls[2].Model.ShouldBe(new AgentsOptions().Requirement.Model);
    }

    [Fact]
    public async Task Discovery_OverBudgetOnAServerProvider_IsBlocked()
    {
        var factory = Substitute.For<ILlmClientFactory>();
        var client = Substitute.For<ILlmClient>();
        client.Provider.Returns("Claude");
        factory.Create(Arg.Any<string>()).Returns(client);
        var options = Options.Create(new AgentsOptions());
        var requirement = new RequirementAgent(factory, options, AgentTestHelpers.Validator, AgentTestHelpers.NewCollector(),
            NullLogger<RequirementAgent>.Instance);
        var decomposer = new TicketDecomposerAgent(factory, options, NullLogger<TicketDecomposerAgent>.Instance);

        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            var ex = await Should.ThrowAsync<LlmException>(() =>
                new IdeaBootstrapService(requirement, decomposer, options, OverBudget()).GenerateAsync("A shop"));
            ex.Message.ShouldContain("budget");
        }
        await client.DidNotReceive().SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>());
    }

    // Practices of one (tenant, repo); everything else is empty. ListAsync is the interface default over GraphAsync.
    private sealed class PracticesMemory(string tenant, string repo, string practice) : IMemoryStore
    {
        public Task<Guid> RecordAsync(string tenantId, MemoryNodeInput node, CancellationToken ct = default) => Task.FromResult(Guid.Empty);
        public Task LinkAsync(string tenantId, Guid fromId, Guid toId, string rel, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MemoryNode>> RecallLessonsAsync(string tenantId, string repoKey, string query, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MemoryNode>>([]);
        public Task<MemoryGraph> GraphAsync(string tenantId, string? repoKey, int maxNodes, CancellationToken ct = default) =>
            Task.FromResult(tenantId == tenant && repoKey == repo
                ? new MemoryGraph([new MemoryNode(Guid.NewGuid(), MemoryKinds.Practice, "p1", practice, practice, repo, false, DateTimeOffset.UtcNow, null)], [])
                : new MemoryGraph([], []));
        public Task SetPinnedAsync(string tenantId, Guid nodeId, bool pinned, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetAsync(string tenantId, Guid nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
