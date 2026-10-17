// Workspace connection safety setting for the UI-first registry (ADR-0011). Platform-scope: which
// self-hosted Git hosts the server may call is a platform-wide SSRF-surface decision. Values augment the
// built-in public hosts (github.com, dev.azure.com) and any Workspaces:AllowedHosts from appsettings.

using System.Collections.Generic;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.Workspaces.Security;

namespace AgentOs.Modules.Workspaces;

/// <summary>Declares <c>Workspaces:*</c> settings.</summary>
public sealed class WorkspacesSettings : ISettingsContributor
{
    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return new(WorkspaceHostPolicy.SettingKey, SettingKind.Text, "Safety", "Allowed self-hosted Git hosts")
        {
            Scope = SettingScope.Platform,
            Help = "GitHub Enterprise / Azure DevOps Server hosts the server may contact, comma-separated. Public github.com / dev.azure.com are always allowed.",
            Validator = WorkspaceHostPolicy.Validate,
        };
    }
}
