// Tool-governance settings for the UI-first registry (ADR-0011). Platform-scope: the fail-closed default
// applies to every tenant, so only a platform admin can change it (and production pins it via
// Settings:Locks). The default is environment-derived, exactly as before: enforce in Production,
// permissive in Development.

using System.Collections.Generic;
using System.Globalization;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Tools;

namespace AgentOs.Modules.Tools;

/// <summary>Declares <c>Tools:*</c> settings.</summary>
public sealed class ToolsSettings(bool enforceByDefault) : ISettingsContributor
{
    /// <summary>Deny tool calls unless a tenant has an explicit allowlist.</summary>
    public const string EnforceByDefault = "Tools:EnforceByDefault";

    /// <summary>Per-call budget for a tool result sent back to the model, in tokens (≈ chars / 4).</summary>
    public const string MaxOutputTokens = ToolOutputShaper.MaxOutputTokensKey;

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return new(EnforceByDefault, SettingKind.Boolean, "Safety", "Fail-closed tool policy")
        {
            DefaultValue = enforceByDefault ? "true" : "false",
            Scope = SettingScope.Platform,
            Help = "Deny every tool call unless the tenant has an explicit allowlist (Tool policy). Keep on in production.",
        };
        yield return new(MaxOutputTokens, SettingKind.WholeNumber, "Tools", "Tool output budget (tokens)")
        {
            DefaultValue = ToolOutputShaper.DefaultMaxOutputTokens.ToString(CultureInfo.InvariantCulture),
            Min = ToolOutputShaper.MinMaxOutputTokens,
            Max = ToolOutputShaper.MaxMaxOutputTokens,
            Help = "Largest tool result (≈ chars / 4) sent back to the model; smaller results pass unchanged. A larger build/test "
                + "log is first trimmed to errors, failed tests and summaries, and JSON keeps its shape; anything still larger is "
                + "cut head + tail with an explicit omitted-lines marker. The evidence log keeps the full output.",
        };
    }
}
