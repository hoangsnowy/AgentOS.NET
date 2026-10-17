// Synchronous settings reads for the LLM gateway's sync seams (ILlmClientFactory.Create is sync and called on
// every agent turn). ISettings reads ride the AppConfig store's per-process cache, so the ValueTask is
// usually already complete — take the fast path and only block on a cold cache miss (the same trade-off
// RuntimeOverrides already makes for its sync getters).

using System;
using AgentOs.Domain.Configuration;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.Llm;

/// <summary>Sync access to <see cref="ISettings"/> for the current tenant.</summary>
public static class LiveSettings
{
    /// <summary>The effective non-empty value of <paramref name="key"/>, or null.</summary>
    public static string? Read(ISettings settings, string key, string? tenantId)
    {
        var pending = settings.GetAsync(key, tenantId);
        var value = pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
        return value.HasValue ? value.Value : null;
    }

    /// <summary>Boolean setting, or <paramref name="fallback"/> when unset/unparsable.</summary>
    public static bool ReadBool(ISettings settings, string key, string? tenantId, bool fallback) =>
        bool.TryParse(Read(settings, key, tenantId), out var b) ? b : fallback;

    /// <summary>The tenant a gateway call runs for: the ambient identity (circuits, background runs), else the
    /// request's tenant context, else the default tenant — the shared <see cref="AmbientIdentity.Resolve"/> order.</summary>
    public static string CurrentTenant(IServiceProvider services)
    {
        if (AmbientIdentity.Current?.TenantId is { Length: > 0 } ambient)
        {
            return ambient;
        }
        using var scope = services.CreateScope();
        return AmbientIdentity.Resolve(null, null, scope.ServiceProvider.GetService<ITenantContext>()).TenantId;
    }
}
