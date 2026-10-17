// Per-tenant tool allowlist read from the encrypted AppConfig KV store. Enforcement defaults are
// environment-driven (ToolsModule): FAIL-CLOSED in Production (deny unless the tool is on the tenant's
// allowlist), permissive in Development / when no config store is wired (unit tests, no-DB standalone)
// so the gate adds zero friction until an admin turns it on in the Policy app.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;
using AgentOs.Modules.AppConfig;
using AgentOs.SharedKernel.Identity;

namespace AgentOs.Modules.Tools.Policy;

internal sealed class AppConfigToolPolicy : IToolPolicy
{
    /// <summary>AppConfig key: "true" turns on hard allowlist enforcement for the tenant.</summary>
    internal const string EnforceKey = "tools/enforce";

    /// <summary>AppConfig key: comma-separated list of allowed tool names.</summary>
    internal const string AllowlistKey = "tools/allowlist";

    private readonly IAppConfigStore? _config;
    private readonly bool _enforceByDefaultFallback;
    private readonly AgentOs.Domain.Configuration.ISettings? _settings;

    public AppConfigToolPolicy(IAppConfigStore? config, bool enforceByDefault = false, AgentOs.Domain.Configuration.ISettings? settings = null)
    {
        _config = config;
        _enforceByDefaultFallback = enforceByDefault;
        _settings = settings;
    }

    // The platform fail-closed default, live from Settings (platform admin) when the registry is wired.
    private async ValueTask<bool> EnforceByDefaultAsync(CancellationToken ct) =>
        _settings is null
            ? _enforceByDefaultFallback
            : await AgentOs.Domain.Configuration.SettingsExtensions.GetBoolAsync(
                _settings, ToolsSettings.EnforceByDefault, null, _enforceByDefaultFallback, ct).ConfigureAwait(false);

    public async Task<ToolPolicyDecision> EvaluateAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_config is null)
        {
            return ToolPolicyDecision.Allow; // no config store wired → permissive
        }

        // Fail closed on a blank tenant: never resolve the shared `default` tenant's allowlist for a
        // request that carries no tenant (a malformed token / off-box call). When enforcement is the
        // platform default, deny; otherwise stay permissive for the dev / no-DB path.
        var enforceByDefault = await EnforceByDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(request.TenantId))
        {
            return enforceByDefault
                ? ToolPolicyDecision.Deny("Tool call carries no tenant; refusing under fail-closed policy.")
                : ToolPolicyDecision.Allow;
        }

        var tenant = request.TenantId;

        // Per-tenant enforce flag wins; when unset, fall back to the global default. Global on + no
        // per-tenant allowlist ⇒ deny (fail-closed).
        var enforce = await _config.GetForTenantAsync(tenant, EnforceKey, cancellationToken).ConfigureAwait(false);
        var enforcing = string.IsNullOrWhiteSpace(enforce)
            ? enforceByDefault
            : string.Equals(enforce, "true", StringComparison.OrdinalIgnoreCase);
        if (!enforcing)
        {
            return ToolPolicyDecision.Allow; // enforcement off → permissive
        }

        var raw = await _config.GetForTenantAsync(tenant, AllowlistKey, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        var allowed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return allowed.Any(t => string.Equals(t, request.ToolName, StringComparison.OrdinalIgnoreCase))
            ? ToolPolicyDecision.Allow
            : ToolPolicyDecision.Deny($"Tool '{request.ToolName}' is not in this tenant's allowlist.");
    }
}
