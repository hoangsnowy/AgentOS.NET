// RBAC v2 (E1.2) — the permissions AgentOS.NET checks. A [Flags] set so a user's effective grant is one
// long: the role → permission lookup is a FrozenDictionary hit per role claim, the check is a single AND.
// The matrix that maps roles onto these lives in exactly one place: PermissionMatrix.

using System;

namespace AgentOs.SharedKernel.Identity;

/// <summary>A capability an endpoint or a desktop app requires. Roles grant sets of these
/// (<see cref="PermissionMatrix"/>); a user's grant is the union over their roles.</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Permission is the domain term (RBAC); not a System.Security permission type.")]
public enum Permission : long
{
    /// <summary>No permission.</summary>
    None = 0,

    /// <summary>Read boards, tickets, tasks, runs, insights, files and the run log.</summary>
    ViewWork = 1L << 0,

    /// <summary>Start and stop agent runs, create and edit tickets and tasks, pair a runner, upload ticket files.</summary>
    RunAgents = 1L << 1,

    /// <summary>Approve or reject what an approval policy holds (E1.5: plan, diff, pull request, merge) and decide the design
    /// check of anyone's task. Approvers and admins; board-scoped like every permission.</summary>
    ApproveWork = 1L << 2,

    /// <summary>Connect and remove boards and their repositories, switch Autopilot, edit ticket flows.</summary>
    ManageWorkspaces = 1L << 3,

    /// <summary>Invite and remove members, change their roles and their board (workspace) memberships.</summary>
    ManageMembers = 1L << 4,

    /// <summary>Tenant settings: model keys, agent profiles, prompts, tool policy, MCP servers.</summary>
    ManageSettings = 1L << 5,

    /// <summary>Create and revoke API tokens (service accounts).</summary>
    ManageApiTokens = 1L << 6,

    /// <summary>Read the audit log and the tool-invocation evidence trail (and export the audit CSV).</summary>
    ViewAudit = 1L << 7,

    /// <summary>Export the tenant's data (Settings → Data) and the cost CSV.</summary>
    ExportData = 1L << 8,
}
