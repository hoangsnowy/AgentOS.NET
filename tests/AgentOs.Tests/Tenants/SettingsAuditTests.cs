// Plan C.3 — settings changes are audited, redacted, and viewable/exportable:
//   • the ISettings path writes `setting.changed` rows ("key: old → new", secrets already masked, platform scope
//     filed under a tenant id no tenant can own);
//   • every direct store write (LLM keys, PATs, prompts, policy, budget) is audited WITHOUT its value, except a
//     non-secret governance allowlist; catalogued keys are left to the ISettings path (no double rows);
//   • the CSV export neutralises spreadsheet formulas.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Settings;
using AgentOs.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class SettingsAuditTests
{
    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEntry> Rows { get; } = [];
        public Task WriteAsync(AuditEntry entry, CancellationToken ct = default) { Rows.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<AuditEntry>> ListAsync(string tenantId, int max = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditEntry>>(Rows.FindAll(r => r.TenantId == tenantId));
    }

    private sealed class CapturingSink : ISettingsAuditSink
    {
        public List<SettingChange> Changes { get; } = [];
        public ValueTask RecordAsync(SettingChange change, CancellationToken cancellationToken = default) { Changes.Add(change); return ValueTask.CompletedTask; }
    }

    private sealed class Catalog(params string[] keys) : ISettingsCatalog
    {
        public IReadOnlyList<SettingDescriptor> All { get; } = Array.ConvertAll(keys, k => new SettingDescriptor(k, SettingKind.Text, "c", k));
        public bool TryGet(string key, out SettingDescriptor descriptor)
        {
            descriptor = Array.Find((SettingDescriptor[])All, d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))!;
            return descriptor is not null;
        }
    }

    private static readonly DateTimeOffset At = new(2026, 10, 17, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sink_WritesSettingChanged_WithOldAndNew()
    {
        var log = new CapturingAuditLog();
        var sp = new ServiceCollection().AddSingleton<IAuditLog>(log).BuildServiceProvider();
        var sink = new AuditLogSettingsSink(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditLogSettingsSink>.Instance);

        await sink.RecordAsync(new SettingChange("Agents:Coding:Model", SettingScope.Tenant, "acme", "alice", "claude-sonnet-4-6", "claude-opus-5", At), TestContext.Current.CancellationToken);
        await sink.RecordAsync(new SettingChange("Platform:AllowedHosts", SettingScope.Platform, null, "root", null, "github.com", At), TestContext.Current.CancellationToken);

        log.Rows[0].ShouldBe(log.Rows[0] with { TenantId = "acme", UserId = "alice", Action = "setting.changed", Target = "Agents:Coding:Model: claude-sonnet-4-6 → claude-opus-5" });
        log.Rows[1].TenantId.ShouldBe(AuditScopes.Platform);
        log.Rows[1].Target.ShouldBe("Platform:AllowedHosts: ∅ → github.com");
        SignupValidation.ValidateTenantId(AuditScopes.Platform).Ok.ShouldBeFalse();   // no tenant can ever own it
    }

    [Fact]
    public void Sink_Target_IsClippedToTheColumn()
        => AuditLogSettingsSink.Target(new SettingChange("k", SettingScope.Tenant, "t", "a", null, new string('x', 600), At)).Length.ShouldBe(256);

    [Fact]
    public async Task Sink_NeverThrows_WhenTheLogIsMissing()
    {
        var sp = new ServiceCollection().BuildServiceProvider();   // no IAuditLog registered
        var sink = new AuditLogSettingsSink(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditLogSettingsSink>.Instance);
        await Should.NotThrowAsync(async () => await sink.RecordAsync(new SettingChange("k", SettingScope.Tenant, "t", "a", null, "v", At)));
    }

    private static (AuditingAppConfigStore Store, CapturingSink Sink) Store(params string[] catalogKeys)
    {
        var sink = new CapturingSink();
        var sp = new ServiceCollection()
            .AddSingleton<ISettingsAuditSink>(sink)
            .AddSingleton<ISettingsCatalog>(new Catalog(catalogKeys))
            .BuildServiceProvider();
        return (new AuditingAppConfigStore(new InMemoryAppConfigStore(), sp), sink);
    }

    [Fact]
    public async Task Store_DirectWrite_IsAudited_WithoutTheValue()
    {
        var (store, sink) = Store();
        await store.SetForTenantAsync("acme", "llm/claude/api-key", "sk-ant-secret-123", TestContext.Current.CancellationToken);
        await store.DeleteForTenantAsync("acme", "llm/claude/api-key", TestContext.Current.CancellationToken);

        sink.Changes.Count.ShouldBe(2);
        sink.Changes[0].TenantId.ShouldBe("acme");
        sink.Changes[0].NewValue.ShouldBe("set (17 chars, value hidden)");
        sink.Changes[1].NewValue.ShouldBe("cleared");
        sink.Changes.ShouldAllBe(c => c.NewValue == null || !c.NewValue.Contains("sk-ant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Store_GovernanceKeys_ShowTheirValue()
    {
        var (store, sink) = Store();
        await store.SetForTenantAsync("acme", "tools/enforce", "true", TestContext.Current.CancellationToken);
        sink.Changes.ShouldHaveSingleItem().NewValue.ShouldBe("true");
    }

    [Fact]
    public async Task Store_CataloguedKeys_AreLeftToTheSettingsPath()
    {
        var (store, sink) = Store("Agents:Coding:Model");
        await store.SetForTenantAsync("acme", "Agents:Coding:Model", "x", TestContext.Current.CancellationToken);
        await store.SetForTenantAsync("__platform", "@platform/Agents:Coding:Model", "y", TestContext.Current.CancellationToken);
        sink.Changes.ShouldBeEmpty();
        (await store.GetForTenantAsync("acme", "Agents:Coding:Model", TestContext.Current.CancellationToken)).ShouldBe("x");   // writes still pass through
    }

    [Fact]
    public void Csv_QuotesAndNeutralisesFormulas()
    {
        var csv = AuditCsv.ToCsv(
        [
            new AuditEntry(Guid.Empty, "acme", "=cmd|'/c calc'!A1", "setting.changed", "k: a, \"b\" → c", null, null, At),
        ]);
        csv.ShouldBe("timestamp_utc,tenant,user,action,detail\r\n2026-10-17T22:00:00Z,acme,'=cmd|'/c calc'!A1,setting.changed,\"k: a, \"\"b\"\" → c\"\r\n");
    }

    [Theory]
    [InlineData("setting.changed", "settings")]
    [InlineData("member.invited", "members")]
    [InlineData("invitation.minted", "members")]
    [InlineData("login.failed", "signin")]
    [InlineData("signup.completed", "signin")]
    [InlineData("something.else", "other")]
    public void AuditLogApp_KindOf_GroupsActions(string action, string kind) => AuditLogApp.KindOf(action).ShouldBe(kind);
}
