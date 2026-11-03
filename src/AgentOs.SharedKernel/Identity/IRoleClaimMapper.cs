// RBAC v2 (E1.2) — the group / claim → role mapping hook. Every authenticated request runs the registered mappers
// (Identity module's RoleClaimsTransformation) and adds the roles they return as ClaimTypes.Role claims, so the
// permission matrix sees them like realm roles. E1.1 (corporate SSO) registers a mapper that turns IdP groups
// (Entra ID / Okta group claims) into AgentOS.NET roles; nothing else in the RBAC stack has to change.

using System.Collections.Generic;
using System.Security.Claims;

namespace AgentOs.SharedKernel.Identity;

/// <summary>Maps claims on a signed-in principal (IdP groups, legacy roles) onto AgentOS.NET roles.</summary>
/// <remarks>Implementations are singletons and run on every authenticated request: keep them allocation-light and
/// side-effect free. Return only canonical role names (<see cref="UserRoles"/>); unknown names grant nothing.</remarks>
public interface IRoleClaimMapper
{
    /// <summary>The extra roles <paramref name="user"/> should hold. May return roles it already holds.</summary>
    IEnumerable<string> MapRoles(ClaimsPrincipal user);
}
