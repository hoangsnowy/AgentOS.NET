// Resolves the build verifier's effective options per build (ADR-0011): live from the settings registry when
// it is wired (Settings → Platform → Build sandbox), else the options computed at startup. Unparsable values
// fall back field by field to the startup defaults — the registry validates writes, this is belt and braces.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.Integration;

/// <summary>Supplies the options for the next build.</summary>
public interface IBuildVerifierOptionsSource
{
    /// <summary>The options to use for one build.</summary>
    ValueTask<BuildVerifierOptions> CurrentAsync(CancellationToken ct = default);
}

/// <summary>Fixed options (tests, hosts without a settings registry).</summary>
public sealed class StaticBuildVerifierOptionsSource(BuildVerifierOptions options) : IBuildVerifierOptionsSource
{
    /// <inheritdoc />
    public ValueTask<BuildVerifierOptions> CurrentAsync(CancellationToken ct = default) => ValueTask.FromResult(options);
}

/// <summary>Options read from platform-scope settings on every build.</summary>
public sealed class SettingsBuildVerifierOptionsSource(ISettings settings, BuildVerifierOptions defaults) : IBuildVerifierOptionsSource
{
    /// <inheritdoc />
    public async ValueTask<BuildVerifierOptions> CurrentAsync(CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        var enabled = await settings.GetBoolAsync(BuildVerifierSettings.Enabled, null, defaults.Enabled, ct).ConfigureAwait(false);
        var sandboxRaw = await settings.GetStringAsync(BuildVerifierSettings.Sandbox, null, null, ct).ConfigureAwait(false);
        var image = await settings.GetStringAsync(BuildVerifierSettings.ContainerImage, null, defaults.ContainerImage, ct).ConfigureAwait(false);
        var cpu = await settings.GetStringAsync(BuildVerifierSettings.CpuLimit, null, null, ct).ConfigureAwait(false);
        var memory = await settings.GetStringAsync(BuildVerifierSettings.MemoryLimit, null, defaults.MemoryLimit, ct).ConfigureAwait(false);
        var pids = await settings.GetIntAsync(BuildVerifierSettings.PidsLimit, null, defaults.PidsLimit, ct).ConfigureAwait(false);
        var timeout = await settings.GetIntAsync(BuildVerifierSettings.TimeoutSeconds, null, defaults.TimeoutSeconds, ct).ConfigureAwait(false);

        return new BuildVerifierOptions(
            Enabled: enabled,
            Sandbox: Enum.TryParse<BuildSandboxMode>(sandboxRaw, ignoreCase: true, out var mode) ? mode : defaults.Sandbox,
            ContainerImage: BuildVerifierSettings.ValidateImage(image ?? string.Empty) is null ? image! : defaults.ContainerImage,
            CpuLimit: double.TryParse(cpu, NumberStyles.Float, inv, out var c) && c > 0 ? c : defaults.CpuLimit,
            MemoryLimit: BuildVerifierSettings.ValidateMemory(memory ?? string.Empty) is null ? memory! : defaults.MemoryLimit,
            PidsLimit: pids > 0 ? pids : defaults.PidsLimit,
            TimeoutSeconds: timeout > 0 ? timeout : defaults.TimeoutSeconds);
    }
}
