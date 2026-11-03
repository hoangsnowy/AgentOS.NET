// Bootstrap keys that hold a list (trusted proxies / networks, allowed hosts, plugin hashes) are set in very different
// places: one env var ("a;b, c"), a compose/Helm list, an appsettings array (Key:0, Key:1). One reader for all of them.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace AgentOs.SharedKernel.Hosting;

/// <summary>Reads a configuration key that holds a list.</summary>
public static class ConfigurationLists
{
    private static readonly char[] Separators = [';', ',', ' ', '\n', '\r', '\t'];

    /// <summary>The entries of <paramref name="key"/>: a single value split on <c>;</c>, <c>,</c> or whitespace, or the
    /// values of its array children (each split the same way). Empty entries are dropped.</summary>
    public static IReadOnlyList<string> Read(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(key);
        IEnumerable<string> values = section.Value is { } scalar ? [scalar] : section.GetChildren().Select(c => c.Value ?? string.Empty);
        return [.. values.SelectMany(v => v.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
    }
}
