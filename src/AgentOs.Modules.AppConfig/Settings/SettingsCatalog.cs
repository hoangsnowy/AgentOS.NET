// The registry of every declared setting (ADR-0011). Built once at startup from the modules'
// ISettingsContributor registrations into a FrozenDictionary: O(1), lock-free lookups on every read.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.AppConfig.Settings;

/// <summary>Immutable catalog of declared settings.</summary>
public sealed class SettingsCatalog : ISettingsCatalog
{
    private readonly FrozenDictionary<string, SettingDescriptor> _byKey;

    /// <summary>Collect every contributor's descriptors. Duplicate keys fail fast — two modules must not own one key.</summary>
    public SettingsCatalog(IEnumerable<ISettingsContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);

        var all = new List<SettingDescriptor>();
        var byKey = new Dictionary<string, SettingDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var contributor in contributors)
        {
            foreach (var d in contributor.Describe())
            {
                ArgumentNullException.ThrowIfNull(d);
                ArgumentException.ThrowIfNullOrWhiteSpace(d.Key);
                if (!byKey.TryAdd(d.Key, d))
                {
                    throw new InvalidOperationException(
                        $"Setting '{d.Key}' is declared twice ({byKey[d.Key].Category} / {d.Category}) — one module must own it.");
                }
                if (d.DefaultValue is not null && SettingValidation.Validate(d, d.DefaultValue) is { } error)
                {
                    throw new InvalidOperationException($"Setting '{d.Key}' has an invalid default: {error}");
                }
                all.Add(d);
            }
        }

        All = all;
        _byKey = byKey.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyList<SettingDescriptor> All { get; }

    /// <inheritdoc />
    public bool TryGet(string key, out SettingDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(key) && _byKey.TryGetValue(key, out var d))
        {
            descriptor = d;
            return true;
        }
        descriptor = null!;
        return false;
    }
}
