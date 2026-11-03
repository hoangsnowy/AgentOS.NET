// RBAC v2 (E1.2) — THE permission matrix. One place maps every role onto its permissions; endpoints (via the
// "perm:<Permission>" authorization policies), desktop apps (AppCatalog / WindowHost / in-app buttons) and services
// (re-checks below the UI, GAP-17) all ask this class, never a role name. Changing what a role may do is a one-line
// edit here, covered by the every-role × every-permission test (PermissionMatrixTests).
//
// Roles (Keycloak realm roles):
//   viewer    — read-only
//   developer — viewer + run agents
//   approver  — viewer + decide human gates (separate from developer: who writes a change is not who approves it)
//   auditor   — viewer + audit log + evidence (read-only compliance)
//   admin     — everything in the tenant
// Roles are additive: a user holding developer + approver gets both sets.
//
// Legacy (pre-E1.2) role kept as an alias so existing users keep working with no migration:
//   member → developer + approver + ManageWorkspaces — everything a member could do before: run agents, decide design
//   checks on their own tasks, and connect boards (the old "Member" policy covered POST /workspaces). Reassigning a
//   legacy member to one of the five roles drops what that role doesn't grant.
//   admin keeps its name. platform-admin is orthogonal (platform-wide settings) and grants nothing tenant-scoped.
//
// Workspace (board) scope: viewer / developer / approver are WORKSPACE-SCOPABLE. A board with no memberships is open —
// tenant roles apply. Once a board has at least one membership it is restricted: a non-admin's workspace-scopable
// roles are replaced there by their membership role (none → no access). admin and auditor are TENANT-WIDE and apply
// on every board.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Claims;

namespace AgentOs.SharedKernel.Identity;

/// <summary>Canonical RBAC v2 role names (Keycloak realm roles).</summary>
public static class UserRoles
{
    /// <summary>Read-only access to the tenant's work.</summary>
    public const string Viewer = "viewer";

    /// <summary>Runs agents and works tickets.</summary>
    public const string Developer = "developer";

    /// <summary>Decides human gates (design checks, approvals).</summary>
    public const string Approver = "approver";

    /// <summary>Read-only compliance: audit log + evidence.</summary>
    public const string Auditor = "auditor";

    /// <summary>Tenant administrator (same realm role name as before E1.2).</summary>
    public const string Admin = ClaimNames.AdminRole;

    /// <summary>Pre-E1.2 role; an alias for <see cref="Developer"/> + <see cref="Approver"/> + managing boards.</summary>
    public const string LegacyMember = ClaimNames.MemberRole;

    /// <summary>The roles an admin can assign, least to most privileged (UI order).</summary>
    public static IReadOnlyList<string> Assignable { get; } = [Viewer, Developer, Approver, Auditor, Admin];

    /// <summary>The roles a board (workspace) membership can carry.</summary>
    public static IReadOnlyList<string> WorkspaceAssignable { get; } = [Viewer, Developer, Approver];

    /// <summary>Every role AgentOS.NET manages on a user (assignable + the legacy alias it replaces). Setting a user's
    /// roles revokes the managed ones not chosen and leaves every other realm role (platform-admin, defaults) alone.</summary>
    public static IReadOnlyList<string> Managed { get; } = [Viewer, Developer, Approver, Auditor, Admin, LegacyMember];

    /// <summary>True for a role an admin may assign.</summary>
    public static bool IsAssignable([NotNullWhen(true)] string? role) => role is not null && PermissionMatrix.AssignableSet.Contains(role);

    /// <summary>True for a role a board membership may carry.</summary>
    public static bool IsWorkspaceAssignable([NotNullWhen(true)] string? role) => role is not null && PermissionMatrix.WorkspaceSet.Contains(role);

    /// <summary>Short UI label for a role.</summary>
    public static string Label(string role) => role switch
    {
        Viewer => "Viewer",
        Developer => "Developer",
        Approver => "Approver",
        Auditor => "Auditor",
        Admin => "Admin",
        LegacyMember => "Member (legacy)",
        _ => role,
    };

    /// <summary>One-line UI description of what a role may do.</summary>
    public static string Describe(string role) => role switch
    {
        Viewer => "Read boards, tasks, runs and insights",
        Developer => "Viewer + run agents, work tickets, pair a computer",
        Approver => "Viewer + decide design checks and approvals",
        Auditor => "Viewer + audit log and evidence (read-only)",
        Admin => "Everything in this workspace: members, settings, tokens, data",
        LegacyMember => "Pre-RBAC role: Developer + Approver + connect boards",
        _ => string.Empty,
    };
}

/// <summary>The single role → permission matrix and the evaluation rules (tenant + workspace scope).</summary>
public static class PermissionMatrix
{
    /// <summary>Every defined permission combined.</summary>
    public const Permission All =
        Permission.ViewWork | Permission.RunAgents | Permission.ApproveWork | Permission.ManageWorkspaces
        | Permission.ManageMembers | Permission.ManageSettings | Permission.ManageApiTokens | Permission.ViewAudit
        | Permission.ExportData;

    private const Permission Viewer = Permission.ViewWork;
    private const Permission Developer = Viewer | Permission.RunAgents;
    private const Permission Approver = Viewer | Permission.ApproveWork;
    private const Permission Auditor = Viewer | Permission.ViewAudit;

    /// <summary>Role → permissions. Ordinal role names (Keycloak role names are case-sensitive).</summary>
    public static FrozenDictionary<string, Permission> Roles { get; } = new Dictionary<string, Permission>(StringComparer.Ordinal)
    {
        [UserRoles.Viewer] = Viewer,
        [UserRoles.Developer] = Developer,
        [UserRoles.Approver] = Approver,
        [UserRoles.Auditor] = Auditor,
        [UserRoles.Admin] = All,
        [UserRoles.LegacyMember] = Developer | Approver | Permission.ManageWorkspaces,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Roles whose grant applies on every board regardless of board memberships.</summary>
    public static FrozenSet<string> TenantWideRoles { get; } =
        new[] { UserRoles.Admin, UserRoles.Auditor }.ToFrozenSet(StringComparer.Ordinal);

    internal static FrozenSet<string> AssignableSet { get; } = UserRoles.Assignable.ToFrozenSet(StringComparer.Ordinal);

    internal static FrozenSet<string> WorkspaceSet { get; } = UserRoles.WorkspaceAssignable.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every single permission flag (no <see cref="Permission.None"/>, no combinations), in declaration order.</summary>
    public static IReadOnlyList<Permission> Each { get; } =
    [
        Permission.ViewWork, Permission.RunAgents, Permission.ApproveWork, Permission.ManageWorkspaces,
        Permission.ManageMembers, Permission.ManageSettings, Permission.ManageApiTokens, Permission.ViewAudit,
        Permission.ExportData,
    ];

    /// <summary>The permissions one role grants; unknown roles grant nothing.</summary>
    public static Permission For(string? role) =>
        role is not null && Roles.TryGetValue(role, out var p) ? p : Permission.None;

    /// <summary>The union of the permissions the given roles grant (tenant scope).</summary>
    public static Permission For(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var granted = Permission.None;
        foreach (var role in roles)
        {
            granted |= For(role);
        }
        return granted;
    }

    /// <summary>True when <paramref name="granted"/> includes every flag of <paramref name="required"/>.</summary>
    public static bool Has(this Permission granted, Permission required) =>
        required != Permission.None && (granted & required) == required;

    /// <summary>The roles that apply on one board (workspace): tenant-wide roles always; the workspace-scopable roles
    /// from the realm on an open board, or the membership role on a restricted one.</summary>
    /// <param name="roles">The user's tenant (realm) roles.</param>
    /// <param name="restricted">True when the board has at least one membership.</param>
    /// <param name="membershipRole">The user's membership role on the board, or <c>null</c>.</param>
    public static IEnumerable<string> EffectiveRoles(IEnumerable<string> roles, bool restricted, string? membershipRole)
    {
        ArgumentNullException.ThrowIfNull(roles);
        foreach (var role in roles.Where(r => !restricted || TenantWideRoles.Contains(r)))
        {
            yield return role;
        }
        // A membership row may only carry a workspace-assignable role — anything else grants nothing.
        if (restricted && UserRoles.IsWorkspaceAssignable(membershipRole))
        {
            yield return membershipRole;
        }
    }

    /// <summary>The effective permissions on one board (workspace) — <see cref="For(IEnumerable{string})"/> over
    /// <see cref="EffectiveRoles"/>.</summary>
    /// <param name="roles">The user's tenant (realm) roles.</param>
    /// <param name="restricted">True when the board has at least one membership.</param>
    /// <param name="membershipRole">The user's membership role on the board, or <c>null</c>.</param>
    public static Permission ForWorkspace(IEnumerable<string> roles, bool restricted, string? membershipRole) =>
        For(EffectiveRoles(roles, restricted, membershipRole));

    /// <summary>The authorization policy name enforcing <paramref name="permission"/> (registered by the Identity module).</summary>
    public static string Policy(Permission permission) => $"{PolicyPrefix}{permission}";

    /// <summary>Prefix of every permission policy name.</summary>
    public const string PolicyPrefix = "perm:";
}

/// <summary>Permission checks on a signed-in principal — the call sites' only way to ask "may this user …".</summary>
public static class PermissionPrincipalExtensions
{
    /// <summary>The realm roles on the principal (every identity's <see cref="ClaimTypes.Role"/> claims).</summary>
    public static IEnumerable<string> GetRoles(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        foreach (var claim in user.FindAll(ClaimTypes.Role))
        {
            yield return claim.Value;
        }
    }

    /// <summary>The tenant-scope permissions the principal's roles grant (none when anonymous).</summary>
    public static Permission GetPermissions(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Identity?.IsAuthenticated == true ? PermissionMatrix.For(user.GetRoles()) : Permission.None;
    }

    /// <summary>True when the signed-in principal holds <paramref name="permission"/> at tenant scope.</summary>
    public static bool Can(this ClaimsPrincipal user, Permission permission) => user.GetPermissions().Has(permission);
}
