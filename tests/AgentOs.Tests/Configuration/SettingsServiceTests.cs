// UI-first settings registry (ADR-0011): precedence lock → tenant → platform → seed → default, scope rules,
// validation, secret masking/redaction, version bumps, and the platform/tenant split on the tenant-agnostic
// in-memory store used for standalone runs.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Pipeline.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Configuration;

public sealed class SettingsServiceTests
{
    private const string Model = "Test:Model";
    private const string Budget = "Test:Budget";
    private const string Secret = "Test:Secret";
    private const string PlatformOnly = "Test:PlatformOnly";

    private sealed class TestSettings : ISettingsContributor
    {
        public IEnumerable<SettingDescriptor> Describe()
        {
            yield return new(Model, SettingKind.Model, "T", "Model") { DefaultValue = "claude-haiku-4-5" };
            yield return new(Budget, SettingKind.Number, "T", "Budget") { DefaultValue = "10", Min = 0, Max = 1000 };
            yield return new(Secret, SettingKind.Secret, "T", "Key");
            yield return new(PlatformOnly, SettingKind.Boolean, "T", "Flag") { DefaultValue = "false", Scope = SettingScope.Platform };
        }
    }

    private sealed class RecordingSink : ISettingsAuditSink
    {
        public List<SettingChange> Changes { get; } = [];

        public ValueTask RecordAsync(SettingChange change, CancellationToken cancellationToken = default)
        {
            Changes.Add(change);
            return ValueTask.CompletedTask;
        }
    }

    private static (SettingsService Svc, RecordingSink Audit) Build(Dictionary<string, string?>? config = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build();
        var audit = new RecordingSink();
        var svc = new SettingsService(
            new SettingsCatalog([new TestSettings()]), new InMemoryAppConfigStore(), cfg, audit,
            NullLogger<SettingsService>.Instance);
        return (svc, audit);
    }

    [Fact]
    public async Task Get_NothingSet_ReturnsDescriptorDefault()
    {
        var (svc, _) = Build();
        (await svc.GetAsync(Model, "acme")).ShouldBe(new SettingValue("claude-haiku-4-5", SettingSource.Default));
    }

    [Fact]
    public async Task Get_Precedence_SeedThenPlatformThenTenantThenLock()
    {
        var config = new Dictionary<string, string?> { [Model] = "seed-model" };
        var (svc, _) = Build(config);
        (await svc.GetAsync(Model, "acme")).Source.ShouldBe(SettingSource.Seed);

        (await svc.SetAsync(Model, "platform-model", SettingScope.Platform, null, "root")).Ok.ShouldBeTrue();
        (await svc.GetAsync(Model, "acme")).ShouldBe(new SettingValue("platform-model", SettingSource.Platform));

        (await svc.SetAsync(Model, "tenant-model", SettingScope.Tenant, "acme", "admin")).Ok.ShouldBeTrue();
        (await svc.GetAsync(Model, "acme")).ShouldBe(new SettingValue("tenant-model", SettingSource.Tenant));
        (await svc.GetAsync(Model, null)).Value.ShouldBe("platform-model");   // platform view ignores tenant rows

        var (locked, _) = Build(new Dictionary<string, string?> { [$"{SettingsService.LocksSection}:{Model}"] = "locked-model" });
        (await locked.GetAsync(Model, "acme")).ShouldBe(new SettingValue("locked-model", SettingSource.Locked));
    }

    [Fact]
    public async Task Set_LockedKey_IsRefused()
    {
        var (svc, _) = Build(new Dictionary<string, string?> { [$"{SettingsService.LocksSection}:{Model}"] = "x" });
        var r = await svc.SetAsync(Model, "claude-opus-5-5", SettingScope.Platform, null, "root");
        r.Ok.ShouldBeFalse();
        r.Error!.ShouldContain("locked");
    }

    [Fact]
    public async Task Set_PlatformOnlyKey_RefusesTenantWrite()
    {
        var (svc, _) = Build();
        (await svc.SetAsync(PlatformOnly, "true", SettingScope.Tenant, "acme", "admin")).Ok.ShouldBeFalse();
        (await svc.SetAsync(PlatformOnly, "true", SettingScope.Platform, null, "root")).Ok.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Budget, "abc")]
    [InlineData(Budget, "5000")]          // above Max
    [InlineData(Model, "gpt 4.1; rm -rf")] // not a model id
    public async Task Set_InvalidValue_IsRejectedAndNotStored(string key, string value)
    {
        var (svc, audit) = Build();
        var before = await svc.GetAsync(key, "acme");

        (await svc.SetAsync(key, value, SettingScope.Tenant, "acme", "admin")).Ok.ShouldBeFalse();

        (await svc.GetAsync(key, "acme")).ShouldBe(before);
        audit.Changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Set_EmptyValue_ClearsOverrideBackToFallback()
    {
        var (svc, _) = Build();
        await svc.SetAsync(Model, "claude-sonnet-5-5", SettingScope.Tenant, "acme", "admin");
        await svc.SetAsync(Model, "  ", SettingScope.Tenant, "acme", "admin");

        (await svc.GetAsync(Model, "acme")).Source.ShouldBe(SettingSource.Default);
    }

    [Fact]
    public async Task Set_BumpsVersion_AndAuditsWithSecretRedacted()
    {
        var (svc, audit) = Build();
        var v0 = svc.Version;

        await svc.SetAsync(Secret, "sk-ant-api03-0123456789abcdef", SettingScope.Tenant, "acme", "admin@acme");

        svc.Version.ShouldBe(v0 + 1);
        var change = audit.Changes.ShouldHaveSingleItem();
        change.Key.ShouldBe(Secret);
        change.TenantId.ShouldBe("acme");
        change.Actor.ShouldBe("admin@acme");
        change.NewValue.ShouldBe("(secret)");
    }

    [Fact]
    public async Task List_MasksSecrets_AndReportsSource()
    {
        var (svc, _) = Build();
        await svc.SetAsync(Secret, "sk-ant-api03-0123456789abcdef", SettingScope.Tenant, "acme", "admin");

        var views = await svc.ListAsync("acme", "T");

        var secret = views.Single(v => v.Descriptor.Key == Secret);
        secret.IsSet.ShouldBeTrue();
        secret.DisplayValue.ShouldBe("••••cdef");
        secret.Source.ShouldBe(SettingSource.Tenant);
        views.Single(v => v.Descriptor.Key == Model).Source.ShouldBe(SettingSource.Default);
    }

    [Fact]
    public async Task Get_UndeclaredKey_Throws()
    {
        var (svc, _) = Build();
        await Should.ThrowAsync<KeyNotFoundException>(async () => await svc.GetAsync("Nope:Key", "acme"));
    }

    [Fact]
    public async Task Set_UndeclaredKey_IsRejected()
    {
        var (svc, _) = Build();
        (await svc.SetAsync("Nope:Key", "x", SettingScope.Platform, null, "root")).Ok.ShouldBeFalse();
    }

    [Fact]
    public void Catalog_DuplicateKey_FailsFast()
        => Should.Throw<InvalidOperationException>(() => new SettingsCatalog([new TestSettings(), new TestSettings()]));

    [Fact]
    public void Catalog_InvalidDefault_FailsFast()
    {
        var bad = new InlineContributor(new SettingDescriptor("X:Y", SettingKind.WholeNumber, "T", "Y") { DefaultValue = "many" });
        Should.Throw<InvalidOperationException>(() => new SettingsCatalog([bad]));
    }

    [Fact]
    public void Catalog_RealModules_BuildWithoutConflicts_AndCoverEveryAgentRole()
    {
        var catalog = new SettingsCatalog([new LlmSettings(), new AgentSettings()]);

        foreach (var (role, defaults) in AgentSettings.Roles)
        {
            catalog.TryGet(AgentSettings.Key(role, "Model"), out var model).ShouldBeTrue();
            // A Board agent's empty seed is "no default" (the computer's / provider's model), never an empty-string value.
            model.DefaultValue.ShouldBe(string.IsNullOrEmpty(defaults.Model) ? null : defaults.Model);
            catalog.TryGet(AgentSettings.Key(role, "MaxTokens"), out var max).ShouldBeTrue();
            max.Max.ShouldBe(AgentSettings.MaxOutputTokensCeiling);
        }
        catalog.TryGet("llm:forceprovider", out _).ShouldBeTrue();   // case-insensitive lookup
        catalog.TryGet(LlmSettings.OfflineFallback, out var offline).ShouldBeTrue();
        offline.Scope.ShouldBe(SettingScope.Platform);
    }

    private sealed class InlineContributor(params SettingDescriptor[] descriptors) : ISettingsContributor
    {
        public IEnumerable<SettingDescriptor> Describe() => descriptors;
    }
}
