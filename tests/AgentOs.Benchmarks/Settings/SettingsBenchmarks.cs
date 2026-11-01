// The cached settings lookup behind ISettings (ADR-0011): every LLM call, policy check and agent profile read goes
// through SettingsService.GetAsync → IAppConfigStore (operator lock → tenant → platform → seed → default). The EF
// store keeps a per-process cache so the steady state never touches the database; here the "database" is the EF
// in-memory provider and every measured read is a cache hit. §2.7 budget: lookup < 100 ns (store) — no DB, no network.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Persistence;
using AgentOs.Modules.AppConfig.Settings;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentOs.Benchmarks.Settings;

/// <summary>Builds a catalog shaped like the real one (~150 descriptors across the module categories).</summary>
internal static class SettingsFixture
{
    public const string Tenant = "acme";
    public const string TenantKey = "Agents:Coding:Model";      // tenant override set
    public const string PlatformKey = "Llm:ForceProvider";      // platform value set
    public const string SeedKey = "Llm:Claude:Model";           // bootstrap config only
    public const string DefaultKey = "Agents:Qa:MaxTokens";     // nothing set → descriptor default (most reads)

    private static readonly string[] Agents =
        ["Orchestrator", "Requirement", "Coding", "Testing", "Qa", "Discovery", "IssueWork", "Reviewer", "Acceptance", "AutopilotOrchestrator", "Retrospective"];

    public static SettingsCatalog Catalog() => new([new Contributor()]);

    /// <summary>Production config is a stack of providers (appsettings, environment, user secrets); a lookup walks them all.</summary>
    public static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["ConnectionStrings:DefaultConnection"] = null,
            [SeedKey] = "claude-sonnet-5-5",
        })
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:CookiePrefix"] = "agentos" })
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Production" })
        .Build();

    private sealed class Contributor : ISettingsContributor
    {
        public IEnumerable<SettingDescriptor> Describe()
        {
            foreach (var agent in Agents)
            {
                yield return new SettingDescriptor($"Agents:{agent}:Provider", SettingKind.Text, "Agent profiles", $"{agent} provider");
                yield return new SettingDescriptor($"Agents:{agent}:Model", SettingKind.Model, "Agent profiles", $"{agent} model");
                yield return new SettingDescriptor($"Agents:{agent}:MaxTokens", SettingKind.WholeNumber, "Agent profiles", $"{agent} max tokens")
                {
                    DefaultValue = "8192", Min = 1, Max = 128_000,
                };
                yield return new SettingDescriptor($"Agents:{agent}:Temperature", SettingKind.Number, "Agent profiles", $"{agent} temperature")
                {
                    DefaultValue = "0.2", Min = 0, Max = 2,
                };
                yield return new SettingDescriptor($"Agents:{agent}:Effort", SettingKind.Choice, "Agent profiles", $"{agent} effort")
                {
                    Choices = ["low", "medium", "high"], DefaultValue = "medium",
                };
            }
            yield return new SettingDescriptor(PlatformKey, SettingKind.Text, "LLM providers", "Force provider");
            yield return new SettingDescriptor(SeedKey, SettingKind.Model, "LLM providers", "Claude model");
            for (var i = 0; i < 90; i++)
            {
                yield return new SettingDescriptor(string.Create(CultureInfo.InvariantCulture, $"Platform:Feature{i}:Enabled"), SettingKind.Boolean, "Platform", $"Feature {i}")
                {
                    DefaultValue = "false",
                };
            }
        }
    }
}

/// <summary><see cref="SettingsService.GetAsync"/> over the in-memory store (standalone mode) — the resolution chain itself.</summary>
[MemoryDiagnoser]
public class SettingsServiceBenchmarks
{
    private SettingsService _settings = null!;
    private readonly string _tenantKey = SettingsFixture.TenantKey;
    private readonly string _platformKey = SettingsFixture.PlatformKey;
    private readonly string _seedKey = SettingsFixture.SeedKey;
    private readonly string _defaultKey = SettingsFixture.DefaultKey;
    private readonly string _tenant = SettingsFixture.Tenant;

    /// <summary>One tenant override + one platform value.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        var store = new InMemoryAppConfigStore();
        _settings = new SettingsService(
            SettingsFixture.Catalog(), store, SettingsFixture.Configuration(), new NullSettingsAuditSink(),
            NullLogger<SettingsService>.Instance);
        (await _settings.SetAsync(_tenantKey, "claude-opus-5-5", SettingScope.Tenant, _tenant, "bench")).Ok.ShouldBeTrue();
        (await _settings.SetAsync(_platformKey, "Claude", SettingScope.Platform, null, "bench")).Ok.ShouldBeTrue();
    }

    /// <summary>Resolved at the tenant layer (shortest chain).</summary>
    [Benchmark]
    public ValueTask<SettingValue> TenantOverride() => _settings.GetAsync(_tenantKey, _tenant);

    /// <summary>Falls through the tenant layer to the platform row.</summary>
    [Benchmark]
    public ValueTask<SettingValue> PlatformValue() => _settings.GetAsync(_platformKey, _tenant);

    /// <summary>Falls through to bootstrap config.</summary>
    [Benchmark]
    public ValueTask<SettingValue> SeedValue() => _settings.GetAsync(_seedKey, _tenant);

    /// <summary>Walks the whole chain to the descriptor default — the most common read.</summary>
    [Benchmark(Baseline = true)]
    public ValueTask<SettingValue> DescriptorDefault() => _settings.GetAsync(_defaultKey, _tenant);

    /// <summary>The synchronous seam used by policy checks and client factories.</summary>
    [Benchmark]
    public string? SyncRead() => _settings.Read(_defaultKey, _tenant);
}

/// <summary><see cref="EfAppConfigStore"/> cache hits — what every read costs once the 15 s cache is warm.</summary>
[MemoryDiagnoser]
public class AppConfigStoreBenchmarks
{
    private ServiceProvider _services = null!;
    private EfAppConfigStore _store = null!;
    private readonly string _tenant = SettingsFixture.Tenant;
    private readonly string _setKey = "Agents:Coding:Model";
    private readonly string _unsetKey = "Agents:Qa:MaxTokens";

    /// <summary>EF in-memory provider stands in for Postgres; both keys are cached before measuring.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppConfigDbContext>(o => o.UseInMemoryDatabase("agentos-bench-appconfig"));
        _services = services.BuildServiceProvider();
        _store = new EfAppConfigStore(_services, new EphemeralDataProtectionProvider(), NullLogger<EfAppConfigStore>.Instance);
        await _store.SetForTenantAsync(_tenant, _setKey, "claude-opus-5-5");
        (await _store.GetForTenantAsync(_tenant, _unsetKey)).ShouldBeNull();
    }

    /// <summary>Dispose the container.</summary>
    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    /// <summary>A cached value.</summary>
    [Benchmark(Baseline = true)]
    public ValueTask<string?> CachedValue() => _store.GetForTenantAsync(_tenant, _setKey);

    /// <summary>A cached miss (unset key) — the most common store read, since most settings use their default.</summary>
    [Benchmark]
    public ValueTask<string?> CachedMiss() => _store.GetForTenantAsync(_tenant, _unsetKey);
}

/// <summary>Tiny guard so a broken fixture fails loudly instead of benchmarking the wrong path.</summary>
internal static class Guard
{
    public static void ShouldBeTrue(this bool value)
    {
        if (!value) { throw new InvalidOperationException("Benchmark fixture setup failed."); }
    }

    public static void ShouldBeNull(this string? value)
    {
        if (value is not null) { throw new InvalidOperationException("Benchmark fixture setup failed."); }
    }
}
