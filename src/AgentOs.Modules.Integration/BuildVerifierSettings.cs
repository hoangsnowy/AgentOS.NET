// build_verifier sandbox settings for the UI-first registry (ADR-0011, ADR-0005). Platform-scope: where and
// how untrusted generated code is built is a platform-wide security decision. Defaults are the
// environment-derived values the module computed before (off in Production until a sandbox is chosen).
// Production pins Sandbox=Container via Settings:Locks so the UI can never downgrade to in-process builds.
//
// ContainerImage and MemoryLimit become `docker run` ARGUMENTS: a value starting with '-' would be parsed as
// a docker flag (e.g. --privileged), so validation is strict and allow-list based, never pattern-matching on
// what to forbid.

using System;
using System.Collections.Generic;
using System.Globalization;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.Integration;

/// <summary>Declares <c>Integration:BuildVerifier:*</c> settings.</summary>
public sealed class BuildVerifierSettings(BuildVerifierOptions defaults) : ISettingsContributor
{
    private const string Category = "Build sandbox";

    /// <summary>Key prefix.</summary>
    public const string Prefix = "Integration:BuildVerifier:";

    /// <summary>Gate.</summary>
    public const string Enabled = Prefix + "Enabled";

    /// <summary>InProcess | Container.</summary>
    public const string Sandbox = Prefix + "Sandbox";

    /// <summary>SDK image for container builds.</summary>
    public const string ContainerImage = Prefix + "ContainerImage";

    /// <summary>docker --cpus.</summary>
    public const string CpuLimit = Prefix + "CpuLimit";

    /// <summary>docker --memory.</summary>
    public const string MemoryLimit = Prefix + "MemoryLimit";

    /// <summary>docker --pids-limit.</summary>
    public const string PidsLimit = Prefix + "PidsLimit";

    /// <summary>Per-build wall-clock limit.</summary>
    public const string TimeoutSeconds = Prefix + "TimeoutSeconds";

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        yield return new(Enabled, SettingKind.Boolean, Category, "Build verification")
        {
            DefaultValue = defaults.Enabled ? "true" : "false",
            Scope = SettingScope.Platform,
            Help = "Let agents compile generated code (build_verifier). Runs untrusted code — enable only with the Container sandbox.",
        };
        yield return new(Sandbox, SettingKind.Choice, Category, "Sandbox")
        {
            DefaultValue = defaults.Sandbox.ToString(),
            Choices = [nameof(BuildSandboxMode.InProcess), nameof(BuildSandboxMode.Container)],
            Scope = SettingScope.Platform,
            Help = "Container = ephemeral docker run with no network, read-only fs, non-root, quotas. InProcess is for local dev only.",
        };
        yield return new(ContainerImage, SettingKind.Text, Category, "SDK image")
        {
            DefaultValue = defaults.ContainerImage,
            Scope = SettingScope.Platform,
            Validator = ValidateImage,
            Help = "Container image used for builds (e.g. mcr.microsoft.com/dotnet/sdk:10.0).",
        };
        yield return new(CpuLimit, SettingKind.Number, Category, "CPU limit")
        {
            DefaultValue = defaults.CpuLimit.ToString(inv),
            Min = 0.25m,
            Max = 16m,
            Scope = SettingScope.Platform,
        };
        yield return new(MemoryLimit, SettingKind.Text, Category, "Memory limit")
        {
            DefaultValue = defaults.MemoryLimit,
            Scope = SettingScope.Platform,
            Validator = ValidateMemory,
            Help = "docker --memory, e.g. 1g or 512m.",
        };
        yield return new(PidsLimit, SettingKind.WholeNumber, Category, "Process limit")
        {
            DefaultValue = defaults.PidsLimit.ToString(inv),
            Min = 32m,
            Max = 4096m,
            Scope = SettingScope.Platform,
        };
        yield return new(TimeoutSeconds, SettingKind.WholeNumber, Category, "Timeout (seconds)")
        {
            DefaultValue = defaults.TimeoutSeconds.ToString(inv),
            Min = 10m,
            Max = 1800m,
            Scope = SettingScope.Platform,
        };
    }

    /// <summary>Image reference: registry/name[:tag][@sha256:digest] — alphanumerics and . _ - / : @ only, and
    /// never starting with '-' (would be read as a docker flag).</summary>
    internal static string? ValidateImage(string value)
    {
        if (value.Length is 0 or > 255 || value[0] is '-' or '.' or '/' or ':' or '@')
        {
            return "Image must be a registry/name[:tag] reference.";
        }
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-' or '/' or ':' or '@'))
            {
                return "Image may only contain lowercase letters, digits and . _ - / : @.";
            }
        }
        return null;
    }

    /// <summary>docker memory value: digits followed by an optional b/k/m/g unit (e.g. 1g, 512m).</summary>
    internal static string? ValidateMemory(string value)
    {
        var span = value.AsSpan();
        var unit = span.Length > 0 && char.ToLowerInvariant(span[^1]) is 'b' or 'k' or 'm' or 'g' ? 1 : 0;
        var digits = span[..^unit];
        if (digits.Length is 0 or > 6)
        {
            return "Memory must look like 1g or 512m.";
        }
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return "Memory must look like 1g or 512m.";
            }
        }
        return null;
    }
}
