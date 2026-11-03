// RBAC v2 (E1.2) — a member's role on one board (workspace). A board with no rows here is open (tenant roles apply);
// the first row makes it restricted (see PermissionMatrix.ForWorkspace). One row per (tenant, board, user).

using System;

namespace AgentOs.Modules.Workspaces.Persistence.Entities;

/// <summary>A user's role on one board (workspace). Tenant-stamped; one row per (tenant, board, user).</summary>
public sealed class WorkspaceMemberEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid WorkspaceId { get; set; }

    /// <summary>The member's stable user id (token <c>sub</c>).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>A workspace-assignable role (<c>viewer</c>, <c>developer</c>, <c>approver</c>).</summary>
    public string Role { get; set; } = string.Empty;

    public string? AddedByUserId { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}
