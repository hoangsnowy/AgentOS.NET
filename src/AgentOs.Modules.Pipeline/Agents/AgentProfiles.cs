// Live agent profiles (ADR-0011, Phase C.4). Agents used to freeze provider/model/temperature/max tokens from
// IOptions<AgentsOptions> in their constructors, so a change needed a restart. They now resolve their profile
// per call through ISettings — lock → tenant → platform → seed (appsettings) → default — so an edit in
// Settings → Agent profiles applies to the next call. Reads ride the AppConfig store's per-process cache.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Resolves an agent's effective profile for the current tenant.</summary>
public interface IAgentProfiles
{
    /// <summary>The profile for <paramref name="role"/> (e.g. <c>Coding</c>); unset or unparsable fields fall back to <paramref name="fallback"/>.</summary>
    ValueTask<AgentOptions> ResolveAsync(string role, AgentOptions fallback, CancellationToken cancellationToken = default);

    /// <summary>The QA-loop iteration cap for the current tenant.</summary>
    ValueTask<int> MaxIterationsAsync(int fallback, CancellationToken cancellationToken = default);

    /// <summary>How long one run of a Board agent may take (an <c>Autopilot:*RunMinutes</c> setting) for the current tenant.</summary>
    ValueTask<TimeSpan> RunLimitAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AutopilotSettings.DefaultRunLimit(key));

    /// <summary>Whether a Board agent run that ended without its JSON reply is asked once for it, for the current tenant
    /// (<see cref="AutopilotSettings.RecoverMissingReplyKey"/>; on by default).</summary>
    ValueTask<bool> RecoverMissingReplyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}

/// <summary><see cref="IAgentProfiles"/> over <see cref="ISettings"/>.</summary>
public sealed class SettingsAgentProfiles : IAgentProfiles
{
    private readonly ISettings _settings;
    private readonly IServiceScopeFactory? _scopes;
    private readonly LegacyAgentSettings? _legacy;

    /// <summary>Create. <paramref name="scopes"/> resolves the request tenant when no ambient identity is pushed;
    /// <paramref name="legacy"/> reads values a renamed role saved under its former key.</summary>
    public SettingsAgentProfiles(ISettings settings, IServiceScopeFactory? scopes = null, LegacyAgentSettings? legacy = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scopes = scopes;
        _legacy = legacy;
    }

    /// <inheritdoc />
    public async ValueTask<AgentOptions> ResolveAsync(string role, AgentOptions fallback, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(fallback);
        var tenant = CurrentTenant();

        var provider = await FieldAsync(role, nameof(AgentOptions.Provider), tenant, cancellationToken).ConfigureAwait(false);
        var model = await FieldAsync(role, nameof(AgentOptions.Model), tenant, cancellationToken).ConfigureAwait(false);
        var temperature = await FieldAsync(role, nameof(AgentOptions.Temperature), tenant, cancellationToken).ConfigureAwait(false);
        var maxTokens = await FieldAsync(role, nameof(AgentOptions.MaxTokens), tenant, cancellationToken).ConfigureAwait(false);

        return new AgentOptions
        {
            Provider = string.IsNullOrWhiteSpace(provider) ? fallback.Provider : provider,
            Model = string.IsNullOrWhiteSpace(model) ? fallback.Model : model,
            Temperature = double.TryParse(temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : fallback.Temperature,
            MaxTokens = int.TryParse(maxTokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : fallback.MaxTokens,
            MaxParallelRepos = fallback.MaxParallelRepos,
        };
    }

    // The field's effective value. Only when nothing is stored for it (it would be the built-in default) does a value
    // the role saved under its former key apply (Decomposer → Discovery).
    private async ValueTask<string?> FieldAsync(string role, string field, string? tenant, CancellationToken ct)
    {
        var value = await _settings.GetAsync(AgentSettings.Key(role, field), tenant, ct).ConfigureAwait(false);
        if (value.Source == SettingSource.Default && _legacy is not null
            && await _legacy.ReadAsync(role, field, tenant, ct).ConfigureAwait(false) is { } legacy)
        {
            return legacy;
        }
        return value.HasValue ? value.Value : null;
    }

    /// <inheritdoc />
    public async ValueTask<int> MaxIterationsAsync(int fallback, CancellationToken cancellationToken = default)
    {
        var n = await _settings.GetIntAsync(AgentSettings.MaxIterations, CurrentTenant(), fallback, cancellationToken).ConfigureAwait(false);
        return n >= 1 ? n : fallback;
    }

    /// <inheritdoc />
    public ValueTask<TimeSpan> RunLimitAsync(string key, CancellationToken cancellationToken = default) =>
        AutopilotSettings.ReadRunLimitAsync(_settings, CurrentTenant(), key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> RecoverMissingReplyAsync(CancellationToken cancellationToken = default) =>
        AutopilotSettings.ReadRecoverMissingReplyAsync(_settings, CurrentTenant(), cancellationToken);

    // Ambient identity first (Blazor circuits and background runs push it); else the request's tenant context.
    private string? CurrentTenant()
    {
        if (AmbientIdentity.Current?.TenantId is { Length: > 0 } ambient)
        {
            return ambient;
        }
        if (_scopes is null)
        {
            return null;
        }
        using var scope = _scopes.CreateScope();
        var tenant = scope.ServiceProvider.GetService<ITenantContext>()?.TenantId;
        return string.IsNullOrWhiteSpace(tenant) ? null : tenant;
    }
}

/// <summary>Fallback when no settings registry is wired (a host that loads Pipeline without AppConfig): the
/// static options are the profile.</summary>
public sealed class StaticAgentProfiles : IAgentProfiles
{
    /// <summary>Shared instance.</summary>
    public static StaticAgentProfiles Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<AgentOptions> ResolveAsync(string role, AgentOptions fallback, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(fallback);

    /// <inheritdoc />
    public ValueTask<int> MaxIterationsAsync(int fallback, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(fallback);
}
