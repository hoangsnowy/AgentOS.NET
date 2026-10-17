// Phase C.5b — platform-wide safety switches read live from the settings registry: the fail-closed tool
// policy, unattended remote execution and the self-hosted Git host allowlist follow a platform admin's
// change on the next check, and an operator lock (production) pins a value the UI cannot change.

using System.Collections.Generic;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Tools;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Tools;
using AgentOs.Modules.Tools.Policy;
using AgentOs.Modules.Workspaces;
using AgentOs.Modules.Workspaces.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Configuration;

public sealed class PlatformSettingsTests
{
    private static (SettingsService Settings, InMemoryAppConfigStore Store) Build(Dictionary<string, string?>? config = null)
    {
        var store = new InMemoryAppConfigStore();
        var settings = new SettingsService(
            new SettingsCatalog([new ToolsSettings(enforceByDefault: false), new RemoteAgentSettings(autoApproveDefault: false), new WorkspacesSettings()]),
            store, new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build(),
            new NullSettingsAuditSink(), NullLogger<SettingsService>.Instance);
        return (settings, store);
    }

    private static ToolInvocationRequest Call(string tenant = "acme") => new("build_verifier", "c1", "{}", tenant);

    [Fact]
    public async Task ToolPolicy_PlatformEnforceByDefault_FlipsLive()
    {
        var (settings, store) = Build();
        var policy = new AppConfigToolPolicy(store, enforceByDefault: false, settings);

        (await policy.EvaluateAsync(Call())).Allowed.ShouldBeTrue();      // permissive default

        (await settings.SetAsync(ToolsSettings.EnforceByDefault, "true", SettingScope.Platform, null, "root")).Ok.ShouldBeTrue();
        var decision = await policy.EvaluateAsync(Call());                  // same instance — no restart
        decision.Allowed.ShouldBeFalse();                                   // fail-closed: no allowlist
    }

    [Fact]
    public async Task ToolPolicy_EnforceByDefault_IsPlatformOnly()
    {
        var (settings, _) = Build();
        (await settings.SetAsync(ToolsSettings.EnforceByDefault, "false", SettingScope.Tenant, "acme", "admin")).Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task RemoteExecApprover_FollowsPlatformSetting()
    {
        var (settings, _) = Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var approver = new ConfigGatedRemoteExecApprover(env, new ConfigurationBuilder().Build(),
            NullLogger<ConfigGatedRemoteExecApprover>.Instance, settings);
        var req = new RemoteExecRequest("r1", "sys", "user", "m");

        (await approver.ApproveAsync(req)).ShouldBeFalse();

        await settings.SetAsync(ConfigGatedRemoteExecApprover.AutoApproveKey, "true", SettingScope.Platform, null, "root");
        (await approver.ApproveAsync(req)).ShouldBeTrue();
    }

    [Fact]
    public async Task OperatorLock_PinsAutoApproveOff_AndRefusesUiWrites()
    {
        var (settings, _) = Build(new Dictionary<string, string?> { ["Settings:Locks:RemoteAgent:AutoApprove"] = "false" });
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var approver = new ConfigGatedRemoteExecApprover(env, new ConfigurationBuilder().Build(),
            NullLogger<ConfigGatedRemoteExecApprover>.Instance, settings);

        (await settings.SetAsync(ConfigGatedRemoteExecApprover.AutoApproveKey, "true", SettingScope.Platform, null, "root")).Ok.ShouldBeFalse();
        (await approver.ApproveAsync(new RemoteExecRequest("r1", "s", "u", "m"))).ShouldBeFalse();
    }

    [Fact]
    public async Task HostPolicy_PlatformAllowedHosts_AddsSelfHostedHostLive_AndKeepsPublicDefaults()
    {
        var (settings, _) = Build();
        var policy = new WorkspaceHostPolicy(Options.Create(new WorkspaceHostOptions()), settings);

        policy.IsAllowed("https://github.mycorp.com").ShouldBeFalse();

        (await settings.SetAsync(WorkspaceHostPolicy.SettingKey, "github.mycorp.com, ado.mycorp.com", SettingScope.Platform, null, "root")).Ok.ShouldBeTrue();

        policy.IsAllowed("https://github.mycorp.com/api/v3").ShouldBeTrue();
        policy.IsAllowed("ado.mycorp.com").ShouldBeTrue();
        policy.IsAllowed("api.github.com").ShouldBeTrue();      // public defaults never dropped
        policy.IsAllowed("evil.example.com").ShouldBeFalse();
    }

    [Fact]
    public async Task HostPolicy_InvalidEntry_IsRejectedAtSave()
    {
        var (settings, _) = Build();
        var r = await settings.SetAsync(WorkspaceHostPolicy.SettingKey, "github.mycorp.com, http://", SettingScope.Platform, null, "root");
        r.Ok.ShouldBeFalse();
    }
}
