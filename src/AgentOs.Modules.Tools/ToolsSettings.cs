// Tool-governance settings for the UI-first registry (ADR-0011). Platform-scope: the fail-closed default
// applies to every tenant, so only a platform admin can change it (and production pins it via
// Settings:Locks). The default is environment-derived, exactly as before: enforce in Production,
// permissive in Development.

using System.Collections.Generic;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.Tools;

/// <summary>Declares <c>Tools:*</c> settings.</summary>
public sealed class ToolsSettings(bool enforceByDefault) : ISettingsContributor
{
    /// <summary>Deny tool calls unless a tenant has an explicit allowlist.</summary>
    public const string EnforceByDefault = "Tools:EnforceByDefault";

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return new(EnforceByDefault, SettingKind.Boolean, "Safety", "Fail-closed tool policy")
        {
            DefaultValue = enforceByDefault ? "true" : "false",
            Scope = SettingScope.Platform,
            Help = "Deny every tool call unless the tenant has an explicit allowlist (Tool policy). Keep on in production.",
        };
    }
}
