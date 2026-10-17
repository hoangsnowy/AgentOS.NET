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
}

/// <summary><see cref="IAgentProfiles"/> over <see cref="ISettings"/>.</summary>
public sealed class SettingsAgentProfiles : IAgentProfiles
{
    private readonly ISettings _settings;
    private readonly IServiceScopeFactory? _scopes;

    /// <summary>Create. <paramref name="scopes"/> resolves the request tenant when no ambient identity is pushed.</summary>
    public SettingsAgentProfiles(ISettings settings, IServiceScopeFactory? scopes = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scopes = scopes;
    }

    /// <inheritdoc />
    public async ValueTask<AgentOptions> ResolveAsync(string role, AgentOptions fallback, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(fallback);
        var tenant = CurrentTenant();

        var provider = await _settings.GetStringAsync(AgentSettings.Key(role, nameof(AgentOptions.Provider)), tenant, null, cancellationToken).ConfigureAwait(false);
        var model = await _settings.GetStringAsync(AgentSettings.Key(role, nameof(AgentOptions.Model)), tenant, null, cancellationToken).ConfigureAwait(false);
        var temperature = await _settings.GetStringAsync(AgentSettings.Key(role, nameof(AgentOptions.Temperature)), tenant, null, cancellationToken).ConfigureAwait(false);
        var maxTokens = await _settings.GetStringAsync(AgentSettings.Key(role, nameof(AgentOptions.MaxTokens)), tenant, null, cancellationToken).ConfigureAwait(false);

        return new AgentOptions
        {
            Provider = string.IsNullOrWhiteSpace(provider) ? fallback.Provider : provider,
            Model = string.IsNullOrWhiteSpace(model) ? fallback.Model : model,
            Temperature = double.TryParse(temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : fallback.Temperature,
            MaxTokens = int.TryParse(maxTokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : fallback.MaxTokens,
            MaxParallelRepos = fallback.MaxParallelRepos,
        };
    }

    /// <inheritdoc />
    public async ValueTask<int> MaxIterationsAsync(int fallback, CancellationToken cancellationToken = default)
    {
        var n = await _settings.GetIntAsync(AgentSettings.MaxIterations, CurrentTenant(), fallback, cancellationToken).ConfigureAwait(false);
        return n >= 1 ? n : fallback;
    }

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
