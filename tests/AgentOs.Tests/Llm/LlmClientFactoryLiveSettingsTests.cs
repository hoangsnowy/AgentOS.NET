// Phase C.5a — the gateway reads provider routing live from the settings registry: a tenant's force provider
// or default provider and the platform offline fallback take effect on the next Create() with no restart,
// and one tenant's routing never leaks to another.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Modularity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class LlmClientFactoryLiveSettingsTests
{
    private static IServiceProvider Build(bool offlineFallbackSeed = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "Claude",
                ["Llm:OfflineFallback"] = offlineFallbackSeed ? "true" : "false",
                ["Llm:Claude:ApiKey"] = "test",
                ["Llm:AzureOpenAi:ApiKey"] = "test",
                ["Llm:AzureOpenAi:Endpoint"] = "https://test.openai.azure.com",
            })
            .Build();
        services.AddModulesFromAssemblies(config, typeof(LlmModule).Assembly, typeof(AgentOs.Modules.AppConfig.AppConfigModule).Assembly);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Create_TenantForceProvider_RoutesThatTenantOnly_WithoutRestart()
    {
        var sp = Build();
        var factory = sp.GetRequiredService<ILlmClientFactory>();
        var settings = sp.GetRequiredService<ISettings>();

        using (AmbientIdentity.Push("acme", "u"))
        {
            factory.Create("Claude").Provider.ShouldBe("Claude");
        }

        (await settings.SetAsync(AppConfigKeys.LlmForceProvider, "AzureOpenAI", SettingScope.Tenant, "acme", "admin")).Ok.ShouldBeTrue();

        using (AmbientIdentity.Push("acme", "u"))
        {
            factory.Create("Claude").Provider.ShouldBe("AzureOpenAI");   // same factory instance — live
        }
        using (AmbientIdentity.Push("globex", "u"))
        {
            factory.Create("Claude").Provider.ShouldBe("Claude");        // other tenant unaffected
        }
    }

    [Fact]
    public async Task CreateDefault_TenantDefaultProvider_IsUsed()
    {
        var sp = Build();
        var factory = sp.GetRequiredService<ILlmClientFactory>();
        await sp.GetRequiredService<ISettings>().SetAsync(LlmSettings.Provider, "AzureOpenAI", SettingScope.Tenant, "acme", "admin");

        using (AmbientIdentity.Push("acme", "u"))
        {
            factory.CreateDefault().Provider.ShouldBe("AzureOpenAI");
        }
    }

    [Fact]
    public async Task Create_PlatformOfflineFallback_TogglesTheCannedSafetyNetLive()
    {
        var sp = Build(offlineFallbackSeed: false);
        var factory = sp.GetRequiredService<ILlmClientFactory>();
        var settings = sp.GetRequiredService<ISettings>();

        using var _ = AmbientIdentity.Push("acme", "u");
        IsChain(factory.Create("Claude")).ShouldBeFalse();

        (await settings.SetAsync(LlmSettings.OfflineFallback, "true", SettingScope.Platform, null, "root")).Ok.ShouldBeTrue();
        IsChain(factory.Create("Claude")).ShouldBeTrue();   // Offline appended to the failover chain

        // Tenants can't flip a platform-only safety switch.
        (await settings.SetAsync(LlmSettings.OfflineFallback, "false", SettingScope.Tenant, "acme", "admin")).Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task Create_InsideBoardAgentRun_NeverFallsOverToTheOfflineDemo()
    {
        // 2026-10-23 on the lab: the member's Claude plan hit its session limit, the Offline client answered New idea
        // with its canned spec, and three junk tickets landed on a real repo. A Board agent run must fail instead.
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:Provider"] = "Claude", ["Llm:OfflineFallback"] = "true" })
            .Build();
        services.AddModulesFromAssemblies(config, typeof(LlmModule).Assembly, typeof(AgentOs.Modules.AppConfig.AppConfigModule).Assembly);
        var limited = Substitute.For<ILlmClient>();
        limited.Provider.Returns("Limited");
        limited.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns<Task<LlmResponse>>(_ => throw new LlmException("You've hit your session limit · resets 11:50pm"));
        services.AddKeyedSingleton("Limited", limited);
        var factory = services.BuildServiceProvider().GetRequiredService<ILlmClientFactory>();
        var request = new LlmRequest("You are the Requirement Agent of an SDLC pipeline.", "An idea", "claude-sonnet-5", MaxTokens: 1000);
        using var _ = AmbientIdentity.Push("acme", "u");

        // Outside a Board run (Workflow demo, standalone) the canned safety net still answers.
        (await factory.Create("Limited").SendAsync(request)).Provider.ShouldBe(OfflineLlmClient.ProviderName);

        using (var run = LlmCallScope.Begin())
        {
            var ex = await Should.ThrowAsync<LlmException>(() => factory.Create("Limited").SendAsync(request));
            ex.Message.ShouldContain("session limit");
            run.Calls.ShouldHaveSingleItem().Success.ShouldBeFalse();   // Insights still sees the failed call
        }
    }

    // FailoverLlmClient is internal; a chain reports the primary's provider, so detect it by type name.
    private static bool IsChain(ILlmClient client) => client.GetType().Name == "FailoverLlmClient";

}
