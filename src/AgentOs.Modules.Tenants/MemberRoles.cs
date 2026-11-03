// RBAC v2 (E1.2) — the role rules member management shares (HTTP endpoints AND the desktop Users app), so the two
// paths can't drift: which roles may be assigned, the lock-out guard, and which role a member "is" in the UI.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentOs.SharedKernel.Identity;

namespace AgentOs.Modules.Tenants;

/// <summary>Validation + display rules for a member's realm roles.</summary>
public static class MemberRoles
{
    /// <summary>The role a new member gets when none is given.</summary>
    public const string DefaultRole = UserRoles.Developer;

    // Most to least privileged — the role a member is shown as in the Users list.
    private static readonly string[] Precedence =
        [UserRoles.Admin, UserRoles.Auditor, UserRoles.Approver, UserRoles.Developer, UserRoles.LegacyMember, UserRoles.Viewer];

    /// <summary>True for a role an invitation / role change may carry (the five RBAC v2 roles, plus the legacy
    /// <c>member</c> alias so pre-E1.2 API clients keep working).</summary>
    public static bool IsAllowed(string? role) => UserRoles.IsAssignable(role) || role == UserRoles.LegacyMember;

    /// <summary>The allowed roles from <paramref name="roles"/>, de-duplicated, in the given order.</summary>
    public static IReadOnlyList<string> Sanitize(IEnumerable<string>? roles) =>
        roles is null ? [] : [.. roles.Where(IsAllowed).Distinct(StringComparer.Ordinal)];

    /// <summary>Why a role change can't be applied, or <c>null</c> when it can.</summary>
    /// <param name="actorUserId">The admin making the change.</param>
    /// <param name="targetUserId">The member whose roles change.</param>
    /// <param name="roles">The sanitized new roles.</param>
    public static string? Check(string? actorUserId, string targetUserId, IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Count == 0)
        {
            return $"roles must contain at least one of: {string.Join(", ", UserRoles.Assignable)}";
        }
        // Lock-out guard: an admin editing themselves must keep the admin role.
        if (string.Equals(actorUserId, targetUserId, StringComparison.Ordinal) && !roles.Contains(UserRoles.Admin, StringComparer.Ordinal))
        {
            return "You cannot remove your own admin role.";
        }
        return null;
    }

    /// <summary>The most privileged managed role the member holds (what the Users list shows), or <c>null</c>.</summary>
    public static string? Primary(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var held = roles as ICollection<string> ?? [.. roles];
        return Array.Find(Precedence, held.Contains);
    }
}
