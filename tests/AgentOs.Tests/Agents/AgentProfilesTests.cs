// Phase C.4 — agents resolve their profile (provider/model/temperature/max tokens) per call through the
// settings registry, so an edit in Settings → Agent profiles applies to the next call without a restart, and
// each tenant gets its own profile.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Agents;

public sealed class AgentProfilesTests
{
    private static readonly string RequirementModel = AgentSettings.Key("Requirement", "Model");
    private static readonly string RequirementProvider = AgentSettings.Key("Requirement", "Provider");

    private sealed record Call(string Provider, LlmRequest Request);

    private static (ILlmClientFactory Factory, List<Call> Calls) RecordingFactory()
    {
        var offline = new OfflineLlmClient();
        var calls = new List<Call>();
        var factory = Substitute.For<ILlmClientFactory>();
        factory.Create(Arg.Any<string>()).Returns(ci =>
        {
            var provider = ci.Arg<string>();
            var client = Substitute.For<ILlmClient>();
            client.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(x =>
            {
                var req = x.Arg<LlmRequest>();
                calls.Add(new Call(provider, req));
                return offline.SendAsync(req, x.Arg<CancellationToken>());
            });
            return client;
        });
        return (factory, calls);
    }

    private static SettingsService Settings() => new(
        new SettingsCatalog([new LlmSettings(), new AgentSettings()]), new InMemoryAppConfigStore(),
        new ConfigurationBuilder().Build(), new NullSettingsAuditSink(), NullLogger<SettingsService>.Instance);

    private static RequirementAgent Agent(ILlmClientFactory factory, IAgentProfiles profiles) => new(
        factory, AgentTestHelpers.OptionsWith(new AgentsOptions()), AgentTestHelpers.Validator,
        AgentTestHelpers.NewCollector(), NullLogger<RequirementAgent>.Instance, profiles: profiles);

    [Fact]
    public async Task Agent_ProfileEditedBetweenCalls_NextCallUsesNewProviderAndModel()
    {
        var settings = Settings();
        var (factory, calls) = RecordingFactory();
        var agent = Agent(factory, new SettingsAgentProfiles(settings));

        using (AmbientIdentity.Push("acme", "u1"))
        {
            await agent.RunAsync(new UserStory("Build a todo app"));
            calls[^1].Request.Model.ShouldBe(new AgentsOptions().Requirement.Model);   // registry default

            (await settings.SetAsync(RequirementModel, "claude-sonnet-5-5", SettingScope.Tenant, "acme", "admin")).Ok.ShouldBeTrue();
            (await settings.SetAsync(RequirementProvider, "Claude", SettingScope.Tenant, "acme", "admin")).Ok.ShouldBeTrue();

            await agent.RunAsync(new UserStory("Build a todo app"));   // same instance — no restart
        }

        calls[^1].Provider.ShouldBe("Claude");
        calls[^1].Request.Model.ShouldBe("claude-sonnet-5-5");
    }

    [Fact]
    public async Task Agent_TenantOverride_DoesNotLeakToAnotherTenant()
    {
        var settings = Settings();
        await settings.SetAsync(RequirementModel, "claude-opus-5-5", SettingScope.Tenant, "acme", "admin");
        var (factory, calls) = RecordingFactory();
        var agent = Agent(factory, new SettingsAgentProfiles(settings));

        using (AmbientIdentity.Push("acme", "u1"))
        {
            await agent.RunAsync(new UserStory("Build a todo app"));
        }
        using (AmbientIdentity.Push("globex", "u2"))
        {
            await agent.RunAsync(new UserStory("Build a todo app"));
        }

        calls[0].Request.Model.ShouldBe("claude-opus-5-5");
        calls[1].Request.Model.ShouldBe(new AgentsOptions().Requirement.Model);
    }

    [Fact]
    public async Task Agent_PlatformValue_AppliesToEveryTenantWithoutOverride()
    {
        var settings = Settings();
        await settings.SetAsync(RequirementModel, "claude-haiku-5-5", SettingScope.Platform, null, "root");
        var (factory, calls) = RecordingFactory();

        using (AmbientIdentity.Push("globex", "u2"))
        {
            await Agent(factory, new SettingsAgentProfiles(settings)).RunAsync(new UserStory("Build a todo app"));
        }

        calls[0].Request.Model.ShouldBe("claude-haiku-5-5");
    }

    [Fact]
    public void Agent_Constructor_NoLongerResolvesAClient()
    {
        // A provider with no key throws on Create; the agent must still construct (it's injected into
        // desktop windows — a throw here would crash the Blazor circuit on open).
        var factory = Substitute.For<ILlmClientFactory>();
        factory.Create(Arg.Any<string>()).Returns(_ => throw new LlmException("no key"));

        Should.NotThrow(() => Agent(factory, StaticAgentProfiles.Instance));
    }

    [Fact]
    public async Task MaxIterations_TenantValue_IsReturned_AndInvalidFallsBack()
    {
        var settings = Settings();
        await settings.SetAsync(AgentSettings.MaxIterations, "5", SettingScope.Tenant, "acme", "admin");
        var profiles = new SettingsAgentProfiles(settings);

        using (AmbientIdentity.Push("acme", "u1"))
        {
            (await profiles.MaxIterationsAsync(3)).ShouldBe(5);
        }
        (await profiles.MaxIterationsAsync(3)).ShouldBe(3);   // no tenant → default
    }
}
