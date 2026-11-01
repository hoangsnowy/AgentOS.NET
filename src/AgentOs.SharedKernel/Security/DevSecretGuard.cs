// Fail-fast at startup if a known committed DEV-DEFAULT secret is still in effect outside Development.
// The AppHost/realm ship throwaway values (admin/admin, the web client secret) for one-command local
// runs; those are public (in git) and must never authenticate a real deployment. Treated as already
// leaked — a Production host carrying any of them refuses to boot rather than run with a known credential.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace AgentOs.SharedKernel.Security;

/// <summary>Startup guard that rejects committed dev-default secrets outside Development.</summary>
public static class DevSecretGuard
{
    // (config key, committed dev-default value). Keep in sync with infra/AgentOs.AppHost/appsettings.json
    // and infra/keycloak/agentic-realm.json.
    /// <summary>The unfilled-secret placeholder of the on-prem templates (deploy/compose/.env.example).</summary>
    public const string Placeholder = "CHANGE_ME";

    private static readonly (string Key, string DevValue)[] KnownDevDefaults =
    [
        ("Auth:Keycloak:ClientSecret", "agentic-web-dev-secret"),
        ("Auth:Keycloak:Admin:Password", "admin"),
    ];

    /// <summary>Throws <see cref="InvalidOperationException"/> if any known dev-default secret, or any value
    /// still holding the <see cref="Placeholder"/>, is present in configuration while <paramref name="environmentName"/> is not Development. No-op in Development.</summary>
    public static void EnsureNoDevDefaults(IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var offenders = new List<string>();
        foreach (var (key, devValue) in KnownDevDefaults)
        {
            if (string.Equals(configuration[key], devValue, StringComparison.Ordinal))
            {
                offenders.Add(key);
            }
        }

        // The on-prem templates (deploy/compose/.env.example) ship every secret as CHANGE_ME for
        // init-secrets.sh to replace — a value that still carries it (alone, or inside a connection string)
        // was never filled in and is just as public as a committed dev default.
        offenders.AddRange(configuration.AsEnumerable()
            .Where(kv => kv.Value is not null && kv.Value.Contains(Placeholder, StringComparison.Ordinal)
                && !offenders.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal));

        if (offenders.Count > 0)
        {
            throw new InvalidOperationException(
                $"Refusing to start in environment '{environmentName}': these settings still hold their "
                + $"committed Development default or the CHANGE_ME placeholder (a publicly-known value): {string.Join(", ", offenders)}. "
                + "Set real secrets via Aspire parameters / azd / user-secrets / environment variables.");
        }
    }
}
