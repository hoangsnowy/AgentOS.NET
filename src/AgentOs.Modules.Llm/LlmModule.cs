// Module entry: binds LlmOptions, registers the key-pool router + every keyed ILlmClient this
// module owns (Claude, AzureOpenAI, MAF), wires the factory + default ILlmClient. The runtime
// overrides are tenant-scoped (RuntimeOverrides reads through IAppConfigStore on every access,
// using the current request's ITenantContext), so no startup hydration step is needed.
// The RemoteAgent provider lives in Modules.RemoteAgent and registers ITSELF as keyed "RemoteAgent".

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.SharedKernel.Modularity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Llm;

public sealed class LlmModule : IModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Code is the single source of defaults (ADR-0011, C.6) — appsettings carries no Llm section. The one
        // environment-dependent default: a fresh Development checkout has no keys, so it answers with
        // labelled offline output instead of failing; everywhere else a missing key fails loudly (and
        // Production also locks it off). Configure runs before Bind, so any explicit value still wins.
        var offlineByDefault = ModuleEnvironment.IsDevelopment(configuration);
        services.AddOptions<LlmOptions>()
            .Configure(o => o.OfflineFallback = offlineByDefault)
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        // UI-first settings (ADR-0011): provider wiring, editable in the desktop.
        services.AddSingleton<AgentOs.Domain.Configuration.ISettingsContributor>(new LlmSettings(offlineByDefault));
        services.AddSingleton<ApiKeyRouter>();
        services.AddSingleton<IRuntimeOverrides, RuntimeOverrides>();

        // MAF — keyed ILlmClient under canonical name.
        services.AddKeyedTransient<ILlmClient, MafChatClient>("MAF");

        // Offline — keyless, deterministic canned output. Always registered (no deps); the factory only puts
        // it on a provider's failover chain when Llm:OfflineFallback is on (standalone dev / E2E / demo).
        services.AddKeyedSingleton<ILlmClient>(OfflineLlmClient.ProviderName, (_, _) => new OfflineLlmClient());

        // Claude (Anthropic.SDK) + Azure OpenAI (Azure.AI.OpenAI) — pooled keyed clients with
        // round-robin + rate-limit failover across the (runtime override + appsettings) key pool.
        services.AddKeyedSingleton<ILlmClient>("Claude", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmOptions>>();
            var ov = sp.GetRequiredService<IRuntimeOverrides>();
            return new PooledChatLlmClient(
                "Claude",
                (key, _model) => SdkChatClients.CreateClaude(key),
                ct => ClaudeKeyPoolAsync(opts.Value.Claude, ov, ct),
                sp.GetRequiredService<ApiKeyRouter>(),
                SdkChatClients.IsRateLimited,
                _ => null,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PooledChatLlmClient>>(),
                baseDelay: null,
                toolRegistry: sp.GetService<AgentOs.Domain.Tools.IToolRegistry>(),
                toolPolicy: sp.GetService<AgentOs.Domain.Tools.IToolPolicy>(),
                toolInvocationLog: sp.GetService<AgentOs.Domain.Tools.IToolInvocationLog>(),
                // Singleton client: pass the root provider so the request-scoped ITenantContext is resolved
                // PER CALL (never captured here — that throws "scoped from root" under scope validation).
                tenantProvider: sp,
                // Classify SDK errors so the key pool fails over on transient (5xx/timeout) + auth errors,
                // not only 429.
                classifyError: SdkChatClients.Classify,
                // An agent profile left on "no model chosen" runs Claude's configured model.
                defaultModel: () => opts.Value.Claude.Model);
        });
        services.AddKeyedSingleton<ILlmClient>("AzureOpenAI", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmOptions>>();
            var ov = sp.GetRequiredService<IRuntimeOverrides>();
            var settings = sp.GetService<AgentOs.Domain.Configuration.ISettings>();
            // Default deployment when a request names none: live from Settings (Llm:AzureOpenAi:Model) when the
            // registry is wired, else the bound option.
            string DefaultDeployment() => settings is null
                ? opts.Value.AzureOpenAi.Model
                : LiveSettings.Read(settings, LlmSettings.AzureModel, LiveSettings.CurrentTenant(sp)) ?? opts.Value.AzureOpenAi.Model;
            return new PooledChatLlmClient(
                "AzureOpenAI",
                (key, model) =>
                {
                    // GAP-01: a Settings endpoint must be an Azure host and is called through SsrfGuard — and never
                    // with the platform's key (re-checked here in case the endpoint changed after the key was chosen).
                    var endpoint = AzureEndpoints.Resolve(ov.AzureEndpoint, opts.Value.AzureOpenAi.Endpoint);
                    AzureEndpoints.EnsureKeyMayGoTo(endpoint, key, PlatformAzureKeys(opts.Value.AzureOpenAi), "AzureOpenAI");
                    return SdkChatClients.CreateAzure(key, endpoint, string.IsNullOrWhiteSpace(model) ? DefaultDeployment() : model);
                },
                ct => AzureKeyPoolAsync(opts.Value.AzureOpenAi, ov, ct),
                sp.GetRequiredService<ApiKeyRouter>(),
                SdkChatClients.IsRateLimited,
                _ => null,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PooledChatLlmClient>>(),
                baseDelay: null,
                toolRegistry: sp.GetService<AgentOs.Domain.Tools.IToolRegistry>(),
                toolPolicy: sp.GetService<AgentOs.Domain.Tools.IToolPolicy>(),
                toolInvocationLog: sp.GetService<AgentOs.Domain.Tools.IToolInvocationLog>(),
                // Singleton client: pass the root provider so the request-scoped ITenantContext is resolved
                // PER CALL (never captured here — that throws "scoped from root" under scope validation).
                tenantProvider: sp,
                // Classify SDK errors so the key pool fails over on transient (5xx/timeout) + auth errors,
                // not only 429.
                classifyError: SdkChatClients.Classify,
                // Fold the tenant's resolved endpoint into the client cache key (read per call from the same
                // override source the factory uses) so two tenants sharing a key but not an endpoint never
                // share a client bound to the wrong Azure resource.
                cacheKeyDiscriminator: () => !string.IsNullOrWhiteSpace(ov.AzureEndpoint) ? ov.AzureEndpoint! : opts.Value.AzureOpenAi.Endpoint,
                // An agent profile left on "no model chosen" runs the tenant's default deployment.
                defaultModel: DefaultDeployment);
        });

        // BYOK vendors (OpenAI, Gemini, xAI): fixed endpoints, tenant-scoped keys read live per call, the profile's
        // model kept when the vendor serves it (else the tenant's default), priced from the CostCalculator table.
        foreach (var vendor in VendorProviders.All)
        {
            services.AddKeyedSingleton<ILlmClient>(vendor.Name, (sp, _) =>
            {
                var settings = sp.GetService<AgentOs.Domain.Configuration.ISettings>();
                string? Read(string key) => settings is null ? null : LiveSettings.Read(settings, key, LiveSettings.CurrentTenant(sp));
                return new PooledChatLlmClient(
                    vendor.Name,
                    (key, model) => SdkChatClients.CreateOpenAiCompatible(key, vendor.Endpoint, model),
                    _ => ValueTask.FromResult<IReadOnlyList<string>>(Read(vendor.KeySetting) is { Length: > 0 } k ? [k] : []),
                    sp.GetRequiredService<ApiKeyRouter>(),
                    SdkChatClients.IsRateLimited,
                    _ => null,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PooledChatLlmClient>>(),
                    baseDelay: null,
                    toolRegistry: sp.GetService<AgentOs.Domain.Tools.IToolRegistry>(),
                    toolPolicy: sp.GetService<AgentOs.Domain.Tools.IToolPolicy>(),
                    toolInvocationLog: sp.GetService<AgentOs.Domain.Tools.IToolInvocationLog>(),
                    tenantProvider: sp,
                    classifyError: SdkChatClients.Classify,
                    modelResolver: requested => vendor.ResolveModel(requested, Read(vendor.ModelSetting)),
                    defaultModel: () => vendor.ResolveModel(null, Read(vendor.ModelSetting)));
            });
        }

        // OpenAI-compatible (E0.8): vLLM / Ollama / LM Studio / Foundry / a corporate gateway. Platform-scoped settings
        // (read live per call), the operator's own prices, and the profile's model mapped to one this server hosts.
        services.AddKeyedSingleton<ILlmClient>(LlmProviders.OpenAiCompatible, (sp, _) =>
        {
            var settings = sp.GetService<AgentOs.Domain.Configuration.ISettings>();
            string? Read(string key) => settings is null ? null : LiveSettings.Read(settings, key, null);
            string Endpoint() => Read(LlmSettings.CompatEndpoint)
                ?? throw new LlmException("No OpenAI-compatible endpoint is configured — a platform admin sets it in Settings → LLM & providers.", LlmProviders.OpenAiCompatible);
            return new PooledChatLlmClient(
                LlmProviders.OpenAiCompatible,
                (key, model) => SdkChatClients.CreateOpenAiCompatible(key, Endpoint(), model),
                _ => ValueTask.FromResult<IReadOnlyList<string>>([Read(LlmSettings.CompatApiKey) is { Length: > 0 } k ? k : SdkChatClients.OpenAiCompatibleNoKey]),
                sp.GetRequiredService<ApiKeyRouter>(),
                SdkChatClients.IsRateLimited,
                _ => null,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PooledChatLlmClient>>(),
                baseDelay: null,
                toolRegistry: sp.GetService<AgentOs.Domain.Tools.IToolRegistry>(),
                toolPolicy: sp.GetService<AgentOs.Domain.Tools.IToolPolicy>(),
                toolInvocationLog: sp.GetService<AgentOs.Domain.Tools.IToolInvocationLog>(),
                tenantProvider: sp,
                classifyError: SdkChatClients.Classify,
                cacheKeyDiscriminator: () => Read(LlmSettings.CompatEndpoint) ?? string.Empty,
                modelResolver: requested => OpenAiCompatibleModels.Resolve(requested, Read(LlmSettings.CompatModel)),
                costOverride: (_, usage) => OpenAiCompatibleModels.Cost(usage, Read(LlmSettings.CompatInputPrice), Read(LlmSettings.CompatOutputPrice)),
                defaultModel: () => Read(LlmSettings.CompatModel));
        });

        services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
        services.AddTransient<ILlmClient>(sp => sp.GetRequiredService<ILlmClientFactory>().CreateDefault());
    }

    // Tenant-isolated key resolution. When the current tenant has configured ITS OWN key(s) via the
    // Settings UI (stored per-tenant in app_config), use ONLY those — the shared appsettings/env platform
    // key is NEVER appended, so tenant A can never silently spend on the platform key (or tenant B's).
    // The platform key is a dev/demo fallback used ONLY when the tenant has no key of its own.
    private static async ValueTask<IReadOnlyList<string>> ClaudeKeyPoolAsync(ClaudeOptions opts, IRuntimeOverrides ov, CancellationToken ct)
    {
        var tenantKeys = await ov.GetAnthropicApiKeysAsync(ct).ConfigureAwait(false);
        return tenantKeys.Count > 0 ? tenantKeys : Collect(opts.ApiKey, opts.ApiKeys);
    }

    // Azure adds one rule (GAP-01): the platform key is only a fallback for the PLATFORM's endpoint. A tenant that set its
    // own endpoint in Settings must also set its own key — else the call is refused before any request is made.
    internal static async ValueTask<IReadOnlyList<string>> AzureKeyPoolAsync(AzureOpenAiOptions opts, IRuntimeOverrides ov, CancellationToken ct)
    {
        var tenantKeys = await ov.GetAzureApiKeysAsync(ct).ConfigureAwait(false);
        var endpoint = AzureEndpoints.Resolve(ov.AzureEndpoint, opts.Endpoint);
        return AzureEndpoints.KeysFor(endpoint, tenantKeys, PlatformAzureKeys(opts), "AzureOpenAI");
    }

    private static List<string> PlatformAzureKeys(AzureOpenAiOptions opts) => Collect(opts.ApiKey, opts.ApiKeys);

    private static List<string> Collect(string? singleKey, IEnumerable<string> pool)
    {
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(singleKey)) { keys.Add(singleKey!); }
        keys.AddRange(pool.Where(k => !string.IsNullOrWhiteSpace(k)));
        return keys.Distinct(StringComparer.Ordinal).ToList();
    }
}
