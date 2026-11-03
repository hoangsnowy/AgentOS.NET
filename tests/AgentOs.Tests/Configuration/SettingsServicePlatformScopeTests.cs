// Security review of PR #326 (RLS stage 2, GAP-14): the "__platform" rows of config.app_config are writable only inside an
// explicit RowLevelSecurity.PlatformScope — that is the database wall. SettingsService.SetAsync(scope: Platform) enters
// that scope ITSELF ("the caller already proved the platform-admin role") without any proof: it takes no principal, no
// role, nothing. Any code path that can reach SetAsync with SettingScope.Platform — the Settings › Routing screen renders
// the platform-scoped Llm:OfflineFallback to EVERY tenant admin as a disabled field whose Save handler is still registered
// (SettingsFields.razor + Btn.razor), and Blazor Server dispatches a circuit event for a disabled element's handler when
// a client sends it — writes platform-wide settings, and the database accepts because the service said "platform".
// The service must never self-elevate: a platform write from a flow that is not a platform operation is refused.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.SharedKernel.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Configuration;

public sealed class SettingsServicePlatformScopeTests
{
    private const string PlatformSwitch = "Test:PlatformSwitch";

    private sealed class TestSettings : ISettingsContributor
    {
        public IEnumerable<SettingDescriptor> Describe()
        {
            yield return new(PlatformSwitch, SettingKind.Boolean, "T", "Platform switch") { DefaultValue = "false", Scope = SettingScope.Platform };
        }
    }

    // Records, for every write, whether the flow was a platform operation at that moment — what the database policy sees.
    private sealed class RecordingStore : IAppConfigStore
    {
        public List<(string Tenant, string Key, string Value, bool InPlatformScope)> Writes { get; } = [];

        public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
        public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        public ValueTask<string?> GetForTenantAsync(string tenantId, string key, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);

        public ValueTask SetForTenantAsync(string tenantId, string key, string value, CancellationToken cancellationToken = default)
        {
            Writes.Add((tenantId, key, value, RowLevelSecurity.InPlatformScope));
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteForTenantAsync(string tenantId, string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class NullSink : ISettingsAuditSink
    {
        public ValueTask RecordAsync(SettingChange change, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SetAsync_PlatformScope_FromAFlowThatIsNotAPlatformOperation_IsRefusedAndNeverSelfElevates()
    {
        // A tenant admin's circuit / request: bound to its own tenant, NOT a platform operation.
        RowLevelSecurity.InPlatformScope.ShouldBeFalse("the test flow must start outside any platform scope");
        var store = new RecordingStore();
        var svc = new SettingsService(
            new SettingsCatalog([new TestSettings()]), store, new ConfigurationBuilder().Build(), new NullSink(),
            NullLogger<SettingsService>.Instance);

        var result = await svc.SetAsync(PlatformSwitch, "true", SettingScope.Platform, tenantId: null, actor: "tenant-admin");

        // The service has no proof the caller is a platform admin, so it must not open the platform rows for them.
        store.Writes.ShouldBeEmpty(
            "SettingsService entered RowLevelSecurity.PlatformScope on its own and wrote the __platform row — "
            + $"the store saw InPlatformScope={store.Writes.Count switch { 0 => "n/a", _ => store.Writes[0].InPlatformScope.ToString() }}");
        result.Ok.ShouldBeFalse("a platform-scope write from an unproven caller must be refused");
        result.Error.ShouldNotBeNull().ShouldContain("platform admin");
    }

    [Fact]
    public async Task SetAsync_PlatformScope_InsideAPlatformOperationEnteredByTheCaller_WritesThePlatformRow()
    {
        var store = new RecordingStore();
        var svc = new SettingsService(
            new SettingsCatalog([new TestSettings()]), store, new ConfigurationBuilder().Build(), new NullSink(),
            NullLogger<SettingsService>.Instance);

        // The surface that verified the platform-admin role enters the scope; the service only follows it.
        SettingWriteResult result;
        using (RowLevelSecurity.PlatformScope("test"))
        {
            result = await svc.SetAsync(PlatformSwitch, "true", SettingScope.Platform, tenantId: null, actor: "platform-admin");
        }

        result.Ok.ShouldBeTrue(result.Error);
        store.Writes.ShouldHaveSingleItem().ShouldBe((SettingsService.PlatformTenant, SettingsService.PlatformPrefix + PlatformSwitch, "true", true));
        RowLevelSecurity.InPlatformScope.ShouldBeFalse("the scope the caller entered must not leak past its dispose");
    }

    [Fact]
    public async Task SetAsync_TenantScope_NeedsNoPlatformOperation()
    {
        var store = new RecordingStore();
        var svc = new SettingsService(
            new SettingsCatalog([new TestSettings(), new TenantSettings()]), store, new ConfigurationBuilder().Build(), new NullSink(),
            NullLogger<SettingsService>.Instance);

        (await svc.SetAsync(TenantKnob, "7", SettingScope.Tenant, "acme", "tenant-admin")).Ok.ShouldBeTrue();

        store.Writes.ShouldHaveSingleItem().ShouldBe(("acme", TenantKnob, "7", false));
    }

    private const string TenantKnob = "Test:TenantKnob";

    private sealed class TenantSettings : ISettingsContributor
    {
        public IEnumerable<SettingDescriptor> Describe()
        {
            yield return new(TenantKnob, SettingKind.WholeNumber, "T", "Tenant knob") { DefaultValue = "1", Min = 0, Max = 100 };
        }
    }
}
