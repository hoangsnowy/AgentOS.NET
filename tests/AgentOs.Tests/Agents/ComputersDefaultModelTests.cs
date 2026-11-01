// "Computer's default": a Board agent chooses NO model unless someone picks one. On a member's computer the request
// reaches the runner with an empty model, so the CLI runs its own (newer) default — no `--model` flag; on our servers
// the LLM gateway fills the provider's configured model before validation; an explicitly chosen model is still passed.
// And the Decomposer → Discovery rename keeps a tenant's saved Decomposer profile and prompt working.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Prompts;
using AgentOs.Modules.RemoteAgent;
using AgentOs.RemoteAgent;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Agents;

public sealed class ComputersDefaultModelTests
{
    private const string Tenant = "acme";
    private static readonly string[] ClaudeArgs = ["-p"];

    private static AcceptanceRequest Change() => new(Guid.NewGuid(), Tenant, "u1",
        new WorkRepo(Guid.NewGuid(), "acme", "shop", "main", "issue-7-ai-fix"), 7, "Checkout", "Pay by card.");

    private const string Approve = """{"verdict":"approve","findings":[],"summary":"ok"}""";

    private static SettingsService Settings(InMemoryAppConfigStore? store = null, IConfiguration? config = null) => new(
        new SettingsCatalog([new LlmSettings(), new AgentSettings(), new AutopilotSettings()]), store ?? new InMemoryAppConfigStore(),
        config ?? new ConfigurationBuilder().Build(), new NullSettingsAuditSink(), NullLogger<SettingsService>.Instance);

    // A factory whose every client is `client`.
    private static ILlmClientFactory FactoryOf(ILlmClient client)
    {
        var factory = Substitute.For<ILlmClientFactory>();
        factory.Create(Arg.Any<string>()).Returns(client);
        return factory;
    }

    [Theory]
    [InlineData(AgentRoles.Discovery)]
    [InlineData(AgentRoles.IssueWork)]
    [InlineData(AgentRoles.Reviewer)]
    [InlineData(AgentRoles.Acceptance)]
    [InlineData(AgentRoles.AutopilotOrchestrator)]
    [InlineData(AgentRoles.Retrospective)]
    public void BoardSeeds_ChooseNoModel_AndStartupValidationAcceptsIt(string role)
    {
        new AgentsOptions().For(role).ShouldNotBeNull().Model.ShouldBeEmpty();
        AgentsOptions.ModelIsOptional(role).ShouldBeTrue();
        new AgentsOptions().IsValid().ShouldBeTrue();
    }

    [Fact]
    public void PipelineAgents_StillNeedAModel()
    {
        AgentsOptions.ModelIsOptional(AgentRoles.Qa).ShouldBeFalse();
        new AgentsOptions { Qa = new AgentOptions { Model = " ", MaxTokens = 10 } }.IsValid().ShouldBeFalse();
    }

    // (a) default profile on RemoteAgent → the runner gets an empty model → the CLI gets no model flag.
    [Fact]
    public async Task DefaultProfile_OnTheMembersComputer_SendsNoModel_AndTheCliGetsNoFlag()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), Tenant, "u1"));
        RemoteExecRequest? sent = null;
        broker.Dispatched += d =>
        {
            sent = d.Request;
            broker.CompleteFromServer(new RemoteExecResult(d.Request.Id, true, Approve, null));
        };
        var remote = new RemoteAgentLlmClient(broker, new ServiceCollection().BuildServiceProvider(), NullLogger<RemoteAgentLlmClient>.Instance);

        ReviewVerdict verdict;
        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            verdict = await new ReviewAgent(FactoryOf(remote), NullLogger<ReviewAgent>.Instance,
                profiles: new SettingsAgentProfiles(Settings())).RunAsync(Change());
        }

        verdict.Ran.ShouldBeTrue();
        sent.ShouldNotBeNull().Model.ShouldBeEmpty();
        CliModel.Flags("claude", sent.Model, ClaudeArgs).ShouldBeEmpty();
        CliModel.Flags("codex", sent.Model, ClaudeArgs).ShouldBeEmpty();
    }

    // (b) default profile on a server provider → the provider's configured model; validation passes.
    [Fact]
    public async Task DefaultProfile_OnAServerProvider_UsesTheProvidersConfiguredModel()
    {
        ChatOptions? seen = null;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seen = ci.ArgAt<ChatOptions>(1);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, Approve)) { Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 } };
            });
        var claude = new PooledChatLlmClient("Claude", (_, _) => chat,
            _ => ValueTask.FromResult<IReadOnlyList<string>>(["k1"]), new ApiKeyRouter(TimeProvider.System),
            _ => false, _ => null, NullLogger.Instance, defaultModel: () => "claude-sonnet-5");
        var settings = Settings();
        (await settings.SetAsync(AgentSettings.Key(AgentRoles.Reviewer, "Provider"), "Claude", SettingScope.Tenant, Tenant, "admin")).Ok.ShouldBeTrue();

        ReviewVerdict verdict;
        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            verdict = await new ReviewAgent(FactoryOf(claude), NullLogger<ReviewAgent>.Instance,
                profiles: new SettingsAgentProfiles(settings)).RunAsync(Change());
        }

        verdict.Ran.ShouldBeTrue();                          // no ArgumentException from LlmRequest.Validate
        seen.ShouldNotBeNull().ModelId.ShouldBe("claude-sonnet-5");
    }

    [Fact]
    public async Task ChatClient_EmptyModel_UsesItsDefault_AndAnExplicitModelWins()
    {
        var models = new List<string?>();
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                models.Add(ci.ArgAt<ChatOptions>(1).ModelId);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            });
        var client = new ChatClientLlmClient(chat, "P", defaultModel: "gpt-5.4");

        await client.SendAsync(new LlmRequest("s", "u", ""));
        await client.SendAsync(new LlmRequest("s", "u", "gpt-4.1"));

        models.ShouldBe(["gpt-5.4", "gpt-4.1"]);
    }

    [Fact]
    public async Task ServerProvider_WithNoModelAnywhere_StillFailsValidation()
    {
        var client = new ChatClientLlmClient(Substitute.For<IChatClient>(), "P");

        await Should.ThrowAsync<ArgumentException>(() => client.SendAsync(new LlmRequest("s", "u", "")));
    }

    // (c) an explicitly chosen model is passed to the CLI.
    [Fact]
    public async Task ChosenModel_OnTheMembersComputer_IsPassedToTheCli()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), Tenant, "u1"));
        RemoteExecRequest? sent = null;
        broker.Dispatched += d =>
        {
            sent = d.Request;
            broker.CompleteFromServer(new RemoteExecResult(d.Request.Id, true, Approve, null));
        };
        var remote = new RemoteAgentLlmClient(broker, new ServiceCollection().BuildServiceProvider(), NullLogger<RemoteAgentLlmClient>.Instance);
        var settings = Settings();
        (await settings.SetAsync(AgentSettings.Key(AgentRoles.Reviewer, "Model"), "claude-opus-5-5", SettingScope.Tenant, Tenant, "admin")).Ok.ShouldBeTrue();

        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            await new ReviewAgent(FactoryOf(remote), NullLogger<ReviewAgent>.Instance, profiles: new SettingsAgentProfiles(settings)).RunAsync(Change());
        }

        sent.ShouldNotBeNull().Model.ShouldBe("claude-opus-5-5");
        CliModel.Flags("claude", sent.Model, ClaudeArgs).ShouldBe(["--model", "claude-opus-5-5"]);
    }

    [Fact]
    public async Task RemoteAgent_NoModel_ReportsTheComputersDefault_InMetrics()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), Tenant, "u1"));
        broker.Dispatched += d => broker.CompleteFromServer(new RemoteExecResult(d.Request.Id, true, "ok", null));
        var remote = new RemoteAgentLlmClient(broker, new ServiceCollection().BuildServiceProvider(), NullLogger<RemoteAgentLlmClient>.Instance);

        LlmResponse response;
        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            response = await remote.SendAsync(new LlmRequest("s", "u", ""));
        }

        response.Model.ShouldBe(RemoteAgentLlmClient.ComputerDefaultModel);
    }

    // ── Decomposer → Discovery: saved values keep working ───────────────────────────────────────

    [Fact]
    public async Task LegacyDecomposerProfile_AppliesToDiscovery_UntilDiscoveryIsSaved()
    {
        var store = new InMemoryAppConfigStore();
        await store.SetForTenantAsync(Tenant, "Agents:Decomposer:Model", "claude-haiku-5-5");
        await store.SetForTenantAsync(Tenant, "Agents:Decomposer:MaxTokens", "1234");
        var settings = Settings(store);
        var profiles = new SettingsAgentProfiles(settings, legacy: new LegacyAgentSettings(store));
        var seed = new AgentsOptions().Discovery;

        AgentOptions before, after, other;
        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            before = await profiles.ResolveAsync(AgentRoles.Discovery, seed);
            (await settings.SetAsync(AgentSettings.Key(AgentRoles.Discovery, "Model"), "claude-opus-5-5", SettingScope.Tenant, Tenant, "admin")).Ok.ShouldBeTrue();
            after = await profiles.ResolveAsync(AgentRoles.Discovery, seed);
        }
        using (AmbientIdentity.Push("globex", "u2"))
        {
            other = await profiles.ResolveAsync(AgentRoles.Discovery, seed);
        }

        before.Model.ShouldBe("claude-haiku-5-5");
        before.MaxTokens.ShouldBe(1234);
        after.Model.ShouldBe("claude-opus-5-5");   // the new key, once saved, wins
        after.MaxTokens.ShouldBe(1234);            // fields not re-saved keep the legacy value
        other.Model.ShouldBeEmpty();               // strictly per tenant
        other.MaxTokens.ShouldBe(seed.MaxTokens);
    }

    [Fact]
    public async Task LegacyDecomposerSeed_InConfiguration_AppliesToDiscovery()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Agents:Decomposer:Provider"] = "AzureOpenAI" }).Build();
        var profiles = new SettingsAgentProfiles(Settings(config: config), legacy: new LegacyAgentSettings(null, config));

        (await profiles.ResolveAsync(AgentRoles.Discovery, new AgentsOptions().Discovery)).Provider.ShouldBe("AzureOpenAI");
    }

    [Fact]
    public async Task LegacyDecomposerProfile_ResetClearsIt()
    {
        var store = new InMemoryAppConfigStore();
        await store.SetForTenantAsync(Tenant, "Agents:Decomposer:Model", "claude-haiku-5-5");
        var legacy = new LegacyAgentSettings(store);

        (await legacy.AnyAsync(AgentRoles.Discovery, Tenant)).ShouldBeTrue();
        await legacy.ClearTenantAsync(AgentRoles.Discovery, Tenant);

        (await legacy.AnyAsync(AgentRoles.Discovery, Tenant)).ShouldBeFalse();
        (await legacy.ReadAsync(AgentRoles.Reviewer, "Model", Tenant)).ShouldBeNull();   // only renamed roles have a former key
    }

    [Fact]
    public async Task LegacyDecomposerPrompt_AppliesToDiscovery_ShowsInPrompts_AndResetClearsIt()
    {
        var store = new InMemoryAppConfigStore();
        await store.SetForTenantAsync(Tenant, "prompt/Decomposer/system", "You are the Decomposition Agent of ACME.");
        var resolver = new AppConfigPromptOverrides(NullLogger<AppConfigPromptOverrides>.Instance, store);
        var service = new PromptOverrideService(store);

        string resolved;
        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            resolved = await resolver.ResolveAsync(AgentRoles.Discovery, DecomposerPrompt.System);
        }
        resolved.ShouldBe("You are the Decomposition Agent of ACME.");
        (await service.GetAsync(Tenant, AgentRoles.Discovery)).ShouldBe("You are the Decomposition Agent of ACME.");

        await service.ClearAsync(Tenant, AgentRoles.Discovery);

        (await service.GetAsync(Tenant, AgentRoles.Discovery)).ShouldBeNull();
        using (AmbientIdentity.Push(Tenant, "u1"))
        {
            (await resolver.ResolveAsync(AgentRoles.Discovery, DecomposerPrompt.System)).ShouldBe(DecomposerPrompt.System);
        }
    }

    [Fact]
    public async Task DiscoveryPromptSaved_WinsOverTheLegacyOne()
    {
        var store = new InMemoryAppConfigStore();
        await store.SetForTenantAsync(Tenant, "prompt/Decomposer/system", "old");
        var service = new PromptOverrideService(store);
        await service.SetAsync(Tenant, AgentRoles.Discovery, "new");

        (await service.GetAsync(Tenant, AgentRoles.Discovery)).ShouldBe("new");
    }
}
