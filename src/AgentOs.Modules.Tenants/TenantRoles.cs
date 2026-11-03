// The roles a tenant member can be given, and what each grants in Keycloak realm roles. "Approver" (E1.5) is a member
// who may also approve or reject the steps an approval policy holds — so it carries the member role with it: an approver
// alone would otherwise fail every Member-policy check.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentOs.SharedKernel.Identity;

namespace AgentOs.Modules.Tenants;

/// <summary>Tenant roles (admin, approver, member) and their realm-role sets.</summary>
public static class TenantRoles
{
    /// <summary>The roles a person can be given, most rights first.</summary>
    public static IReadOnlyList<string> All { get; } = [ClaimNames.AdminRole, ClaimNames.ApproverRole, ClaimNames.MemberRole];

    /// <summary>True for a role a tenant admin may grant.</summary>
    public static bool IsKnown(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);

    /// <summary>The realm roles <paramref name="roles"/> amount to: unknown roles dropped, an approver also a member.</summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string>? roles)
    {
        var set = (roles ?? []).Where(IsKnown).ToList();
        if (set.Contains(ClaimNames.ApproverRole, StringComparer.Ordinal) && !set.Contains(ClaimNames.AdminRole, StringComparer.Ordinal))
        {
            set.Add(ClaimNames.MemberRole);
        }
        return set.Distinct(StringComparer.Ordinal).OrderBy(r => All.ToList().IndexOf(r)).ToList();
    }

    /// <summary>The one role a person is shown with: admin over approver over member.</summary>
    public static string Primary(IEnumerable<string> realmRoles)
    {
        ArgumentNullException.ThrowIfNull(realmRoles);
        var set = realmRoles.ToHashSet(StringComparer.Ordinal);
        return All.FirstOrDefault(set.Contains) ?? ClaimNames.MemberRole;
    }
}
