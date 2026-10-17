// Remote-execution safety setting for the UI-first registry (ADR-0011). Platform-scope: unattended execution
// on paired machines is a platform-wide risk decision. Default is environment-derived, as before: allow in
// Development, deny elsewhere; production also pins it via Settings:Locks.

using System.Collections.Generic;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>Declares <c>RemoteAgent:*</c> settings.</summary>
public sealed class RemoteAgentSettings(bool autoApproveDefault) : ISettingsContributor
{
    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return new(ConfigGatedRemoteExecApprover.AutoApproveKey, SettingKind.Boolean, "Safety", "Unattended remote execution")
        {
            DefaultValue = autoApproveDefault ? "true" : "false",
            Scope = SettingScope.Platform,
            Help = "Let agents run prompts and runner_shell commands on paired machines without a per-run approval. Dev only.",
        };
    }
}
