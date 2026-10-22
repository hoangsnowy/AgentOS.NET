// Who decides Autopilot's next moves on a board (ADR-0011: a UI setting, per tenant). The Orchestrator agent by
// default; "Rules" keeps the fixed rules only (the agent's guardrails), e.g. to save the member's subscription.

using System.Collections.Generic;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Declares <c>Autopilot:DecidedBy</c>.</summary>
public sealed class AutopilotSettings : ISettingsContributor
{
    /// <summary>The setting key.</summary>
    public const string DecidedByKey = "Autopilot:DecidedBy";

    /// <summary>The Orchestrator agent decides (within the rules).</summary>
    public const string Agent = "Orchestrator agent";

    /// <summary>The fixed rules decide.</summary>
    public const string Rules = "Rules";

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return new(DecidedByKey, SettingKind.Choice, "Autopilot", "Decided by")
        {
            DefaultValue = Agent,
            Choices = [Agent, Rules],
            Help = "Orchestrator agent: an AI lead reads the whole project and picks the next moves, retries stuck tasks with guidance and asks you precise questions — always within the rules. Rules: the fixed rules alone.",
        };
    }

    /// <summary>True when the stored value means the agent decides (anything but "Rules").</summary>
    public static bool AgentDecides(string? value) => !string.Equals(value?.Trim(), Rules, System.StringComparison.OrdinalIgnoreCase);
}
