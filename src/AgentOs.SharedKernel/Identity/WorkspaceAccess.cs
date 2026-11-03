// RBAC v2 (E1.2) — board (workspace) scope of the permission matrix, shared by every module that gates per board
// (Workspaces owns the memberships; Sessions, Pipeline and the Web desktop only ask). WorkspaceAccessMap is built once
// per request/circuit from one read of the tenant's memberships, then answers per board in O(1) via frozen lookups.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.SharedKernel.Identity;

/// <summary>One user's role on one board.</summary>
public sealed record WorkspaceMember(Guid WorkspaceId, string UserId, string Role, string? AddedByUserId, DateTimeOffset AddedAtUtc);

/// <summary>A user's effective permissions on every board of a tenant, computed once from one read.</summary>
public sealed class WorkspaceAccessMap
{
    private readonly string[] _roles;
    private readonly FrozenSet<Guid> _restricted;
    private readonly FrozenDictionary<Guid, string> _mine;

    private WorkspaceAccessMap(string[] roles, FrozenSet<Guid> restricted, FrozenDictionary<Guid, string> mine)
    {
        _roles = roles;
        _restricted = restricted;
        _mine = mine;
        TenantPermissions = PermissionMatrix.For(roles);
    }

    /// <summary>The user's tenant-scope permissions (realm roles only).</summary>
    public Permission TenantPermissions { get; }

    /// <summary>Build the map for one user from the tenant's memberships (single pass).</summary>
    public static WorkspaceAccessMap Build(IEnumerable<string> roles, string? userId, IEnumerable<WorkspaceMember> memberships)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(memberships);
        var restricted = new HashSet<Guid>();
        var mine = new Dictionary<Guid, string>();
        foreach (var m in memberships)
        {
            restricted.Add(m.WorkspaceId);
            if (userId is not null && string.Equals(m.UserId, userId, StringComparison.Ordinal))
            {
                mine[m.WorkspaceId] = m.Role;
            }
        }
        return new WorkspaceAccessMap([.. roles], restricted.ToFrozenSet(), mine.ToFrozenDictionary());
    }

    /// <summary>True when the board has at least one membership (only its members and tenant-wide roles get in).</summary>
    public bool IsRestricted(Guid workspaceId) => _restricted.Contains(workspaceId);

    /// <summary>The user's own membership role on the board, or <c>null</c>.</summary>
    public string? MembershipRole(Guid workspaceId) => _mine.GetValueOrDefault(workspaceId);

    /// <summary>The roles that apply to the user on one board (see <see cref="PermissionMatrix.EffectiveRoles"/>).</summary>
    public IEnumerable<string> RolesOn(Guid workspaceId) =>
        PermissionMatrix.EffectiveRoles(_roles, _restricted.Contains(workspaceId), _mine.GetValueOrDefault(workspaceId));

    /// <summary>The user's effective permissions on one board.</summary>
    public Permission For(Guid workspaceId) =>
        PermissionMatrix.ForWorkspace(_roles, _restricted.Contains(workspaceId), _mine.GetValueOrDefault(workspaceId));

    /// <summary>True when the user holds <paramref name="permission"/> on the board.</summary>
    public bool Can(Guid workspaceId, Permission permission) => For(workspaceId).Has(permission);

    /// <summary>True when the user may see the board at all (holds <see cref="Permission.ViewWork"/> there).</summary>
    public bool CanSee(Guid workspaceId) => Can(workspaceId, Permission.ViewWork);
}

/// <summary>Resolves a user's permissions per board.</summary>
public interface IWorkspaceAccess
{
    /// <summary>The user's access to every board of the tenant (one read).</summary>
    Task<WorkspaceAccessMap> ForUserAsync(string tenantId, IEnumerable<string> roles, string? userId, CancellationToken ct = default);
}

/// <summary>Convenience overloads over a signed-in principal.</summary>
public static class WorkspaceAccessExtensions
{
    /// <summary>The principal's access to every board of <paramref name="tenantId"/>.</summary>
    public static Task<WorkspaceAccessMap> ForUserAsync(this IWorkspaceAccess access, string tenantId, ClaimsPrincipal user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(user);
        var roles = user.Identity?.IsAuthenticated == true ? user.GetRoles() : [];
        return access.ForUserAsync(tenantId, roles, user.GetUserId(), ct);
    }
}

