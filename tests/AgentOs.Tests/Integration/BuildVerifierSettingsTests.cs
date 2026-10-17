// Phase C.5b-2 — build_verifier sandbox settings are live (gate, sandbox mode, quotas resolved per build),
// strictly validated (image/memory become `docker run` arguments — nothing that docker would read as a flag),
// and production can pin Sandbox=Container so the UI never downgrades to in-process builds.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.Modules.Integration;
using AgentOs.Modules.Integration.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Integration;

public sealed class BuildVerifierSettingsTests
{
    private static readonly BuildVerifierOptions Defaults = new(Enabled: false);

    private static SettingsService Settings(Dictionary<string, string?>? config = null) => new(
        new SettingsCatalog([new BuildVerifierSettings(Defaults)]), new InMemoryAppConfigStore(),
        new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build(),
        new NullSettingsAuditSink(), NullLogger<SettingsService>.Instance);

    [Theory]
    [InlineData("mcr.microsoft.com/dotnet/sdk:10.0", true)]
    [InlineData("registry.local:5000/sdk@sha256:abc123", true)]
    [InlineData("--privileged", false)]          // would be parsed as a docker flag
    [InlineData("-v /:/host", false)]
    [InlineData("sdk; rm -rf /", false)]
    [InlineData("SDK:Latest", false)]             // image refs are lowercase
    public void ValidateImage_AllowsOnlyImageReferences(string value, bool ok)
        => (BuildVerifierSettings.ValidateImage(value) is null).ShouldBe(ok);

    [Theory]
    [InlineData("1g", true)]
    [InlineData("512m", true)]
    [InlineData("2048", true)]
    [InlineData("-1g", false)]
    [InlineData("1gb", false)]
    [InlineData("", false)]
    public void ValidateMemory_AcceptsDockerMemoryValues(string value, bool ok)
        => (BuildVerifierSettings.ValidateMemory(value) is null).ShouldBe(ok);

    [Fact]
    public async Task OptionsSource_ReflectsPlatformChangesLive()
    {
        var settings = Settings();
        var source = new SettingsBuildVerifierOptionsSource(settings, Defaults);
        (await source.CurrentAsync()).Enabled.ShouldBeFalse();

        await settings.SetAsync(BuildVerifierSettings.Enabled, "true", SettingScope.Platform, null, "root");
        await settings.SetAsync(BuildVerifierSettings.Sandbox, "Container", SettingScope.Platform, null, "root");
        await settings.SetAsync(BuildVerifierSettings.MemoryLimit, "512m", SettingScope.Platform, null, "root");

        var o = await source.CurrentAsync();
        o.Enabled.ShouldBeTrue();
        o.Sandbox.ShouldBe(BuildSandboxMode.Container);
        o.MemoryLimit.ShouldBe("512m");
        o.ContainerImage.ShouldBe(Defaults.ContainerImage);
    }

    [Fact]
    public async Task Settings_RejectFlagLikeImage_AndTenantWrites()
    {
        var settings = Settings();
        (await settings.SetAsync(BuildVerifierSettings.ContainerImage, "--privileged", SettingScope.Platform, null, "root")).Ok.ShouldBeFalse();
        (await settings.SetAsync(BuildVerifierSettings.Enabled, "true", SettingScope.Tenant, "acme", "admin")).Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task ProductionLock_PinsContainerSandbox()
    {
        var settings = Settings(new Dictionary<string, string?> { ["Settings:Locks:Integration:BuildVerifier:Sandbox"] = "Container" });
        (await settings.SetAsync(BuildVerifierSettings.Sandbox, "InProcess", SettingScope.Platform, null, "root")).Ok.ShouldBeFalse();
        (await new SettingsBuildVerifierOptionsSource(settings, Defaults).CurrentAsync()).Sandbox.ShouldBe(BuildSandboxMode.Container);
    }

    [Fact]
    public async Task Verifier_UsesCurrentGateAndRunnerPerBuild()
    {
        var settings = Settings();
        var runner = Substitute.For<ISandboxedBuildRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new BuildVerifyResult(true, 0, "ok", 1));
        var modesSeen = new List<BuildSandboxMode>();
        var verifier = new BuildVerifier(NullLogger<BuildVerifier>.Instance,
            new SettingsBuildVerifierOptionsSource(settings, Defaults),
            opts => { modesSeen.Add(opts.Sandbox); return runner; });
        var files = new[] { new BuildVerifyFile("src/A.cs", "namespace A; public class B {}") };

        (await verifier.VerifyFilesAsync(files, CancellationToken.None)).Success.ShouldBeFalse();   // gate off
        modesSeen.ShouldBeEmpty();

        await settings.SetAsync(BuildVerifierSettings.Enabled, "true", SettingScope.Platform, null, "root");
        await settings.SetAsync(BuildVerifierSettings.Sandbox, "Container", SettingScope.Platform, null, "root");
        await verifier.VerifyFilesAsync(files, CancellationToken.None);

        modesSeen.ShouldBe([BuildSandboxMode.Container]);   // same verifier instance — live
    }
}
