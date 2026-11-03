// RBAC v2 (E1.2) — endpoint convention: `.RequirePermission(Permission.RunAgents)` attaches the "perm:<Permission>"
// policy the Identity module registers. Lives in SharedKernel so a module can gate its routes without referencing the
// Identity module.

using System;
using Microsoft.AspNetCore.Builder;

namespace AgentOs.SharedKernel.Identity;

/// <summary>Attach a permission policy from <see cref="PermissionMatrix"/> to endpoints.</summary>
public static class PermissionEndpointExtensions
{
    /// <summary>Require <paramref name="permission"/> (tenant scope) for the endpoint(s).</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.RequireAuthorization(PermissionMatrix.Policy(permission));
    }
}
