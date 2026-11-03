// Read-only record of what the plugin loader found at startup — surfaced in the desktop Plugins manager.
// A failed load is recorded too (status Failed + Error), so a broken plugin is visible rather than silent.

using System.Collections.Generic;

namespace AgentOs.SharedKernel.Plugins;

/// <summary>Load outcome for one plugin assembly.</summary>
public enum PluginStatus
{
    /// <summary>The plugin loaded and its services were registered.</summary>
    Loaded,

    /// <summary>The assembly failed to load or the plugin threw while registering.</summary>
    Failed,

    /// <summary>The assembly's SHA-256 is not in <c>Plugins:AllowedSha256</c>; none of its code was loaded (GAP-19).</summary>
    Refused,
}

/// <summary>One plugin's load result + a summary of what it contributed.</summary>
/// <param name="Manifest">The plugin's manifest (a synthetic file-name manifest when load failed before reading it).</param>
/// <param name="Status">Whether the plugin loaded.</param>
/// <param name="AssemblyPath">Absolute path of the assembly the plugin was loaded from (empty for in-memory test assemblies).</param>
/// <param name="Capabilities">Human-readable summary of what it registered (e.g. <c>"1 tool"</c>, <c>"provider: Echo"</c>).</param>
/// <param name="Error">Failure or refusal detail when <see cref="Status"/> is not <see cref="PluginStatus.Loaded"/>.</param>
/// <param name="Sha256">The assembly file's SHA-256 (lower-case hex); null for an in-memory (test) assembly.</param>
public sealed record LoadedPlugin(
    PluginManifest Manifest,
    PluginStatus Status,
    string AssemblyPath,
    IReadOnlyList<string> Capabilities,
    string? Error = null,
    string? Sha256 = null);

/// <summary>The set of plugins discovered at startup. Registered as a singleton; read by the desktop.</summary>
public interface IPluginCatalog
{
    /// <summary>Every discovered plugin (loaded + failed), in discovery order.</summary>
    IReadOnlyList<LoadedPlugin> Plugins { get; }
}

/// <summary>Default immutable <see cref="IPluginCatalog"/>.</summary>
public sealed class PluginCatalog : IPluginCatalog
{
    /// <summary>An empty catalog (no plugins folder / nothing discovered).</summary>
    public static readonly PluginCatalog Empty = new([]);

    public PluginCatalog(IReadOnlyList<LoadedPlugin> plugins) => Plugins = plugins;

    /// <inheritdoc />
    public IReadOnlyList<LoadedPlugin> Plugins { get; }
}
