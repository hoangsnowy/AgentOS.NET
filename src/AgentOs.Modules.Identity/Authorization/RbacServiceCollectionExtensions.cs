// RBAC v2 (E1.2) — wires the permission matrix into ASP.NET Core authorization: one "perm:<Permission>" policy per
// Permission (PermissionMatrix.Policy), a handler that evaluates the matrix against the caller's role claims, and the
// claims transformation that runs the IRoleClaimMapper hook (legacy roles today, IdP groups with E1.1).
// Kept out of IdentityModule.cs so that file stays a two-line call site.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentOs.Modules.Identity.Authorization;

/// <summary>Registration of the RBAC v2 policies, handler and claim-mapping hook.</summary>
public static class RbacServiceCollectionExtensions
{
    /// <summary>Register one policy per <see cref="Permission"/> (named <see cref="PermissionMatrix.Policy"/>).</summary>
    public static AuthorizationOptions AddPermissionPolicies(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var permission in PermissionMatrix.Each)
        {
            options.AddPolicy(PermissionMatrix.Policy(permission), policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission)));
        }
        return options;
    }

    /// <summary>Require <paramref name="permission"/> on a policy (e.g. the legacy <c>Member</c> policy).</summary>
    public static AuthorizationPolicyBuilder RequirePermission(this AuthorizationPolicyBuilder policy, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission));
    }

    /// <summary>Register the permission handler + the role-claim mapping hook (with the legacy-role mapper).</summary>
    public static IServiceCollection AddRbac(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, PermissionAuthorizationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRoleClaimMapper, LegacyRoleClaimMapper>());
        services.TryAddTransient<IClaimsTransformation, RoleClaimsTransformation>();
        return services;
    }
}

/// <summary>Requires one permission from the matrix.</summary>
public sealed record PermissionRequirement(Permission Permission) : IAuthorizationRequirement;

/// <summary>Succeeds when the caller's role claims grant the required permission (tenant scope).</summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        if (context.User.Can(requirement.Permission))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

/// <summary>Adds the roles every <see cref="IRoleClaimMapper"/> returns as <see cref="ClaimTypes.Role"/> claims.</summary>
/// <remarks>Runs on every authenticate (both hosts); idempotent — a role the principal already holds is not added
/// twice, and the original principal is never mutated (a fresh identity carries the mapped roles).</remarks>
public sealed class RoleClaimsTransformation : IClaimsTransformation
{
    /// <summary>Authentication type of the identity that carries mapped roles.</summary>
    public const string MappedIdentityType = "agentos-rbac";

    private readonly IRoleClaimMapper[] _mappers;

    public RoleClaimsTransformation(IEnumerable<IRoleClaimMapper> mappers)
    {
        ArgumentNullException.ThrowIfNull(mappers);
        _mappers = [.. mappers];
    }

    /// <inheritdoc />
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (_mappers.Length == 0 || principal.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(principal);
        }

        HashSet<string>? held = null;
        List<string>? added = null;
        foreach (var role in _mappers.SelectMany(m => m.MapRoles(principal)))
        {
            if (!PermissionMatrix.Roles.ContainsKey(role)) { continue; }
            held ??= principal.GetRoles().ToHashSet(StringComparer.Ordinal);
            if (held.Add(role)) { (added ??= []).Add(role); }
        }
        if (added is null)
        {
            return Task.FromResult(principal);
        }

        var clone = principal.Clone();
        clone.AddIdentity(new ClaimsIdentity(
            added.Select(r => new Claim(ClaimTypes.Role, r)), MappedIdentityType, ClaimTypes.Name, ClaimTypes.Role));
        return Task.FromResult(clone);
    }
}

/// <summary>Maps the pre-E1.2 <c>member</c> role onto <c>developer</c>, so role displays and <c>IsInRole("developer")</c>
/// agree with the matrix (which already treats <c>member</c> as developer + managing boards).</summary>
public sealed class LegacyRoleClaimMapper : IRoleClaimMapper
{
    private static readonly string[] MemberRoles = [UserRoles.Developer];

    /// <inheritdoc />
    public IEnumerable<string> MapRoles(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsInRole(UserRoles.LegacyMember) ? MemberRoles : [];
    }
}
