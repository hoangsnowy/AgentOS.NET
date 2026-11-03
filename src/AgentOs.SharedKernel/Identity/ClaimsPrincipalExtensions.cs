// One place that knows HOW identity is encoded in a ClaimsPrincipal — the "tenant" claim type, the
// subject claim (sub, with the NameIdentifier fallback ASP.NET remaps it to), and the admin role name.
// Before this, every host + Blazor component re-hand-rolled FindFirst("tenant") / IsInRole("admin") /
// the sub-then-NameIdentifier dance (16+ sites), so a claim rename was a repo-wide hunt and the sites
// drifted (some fell back to "", some to "default"). The fallback stays at the call site (it is a policy
// choice); only the claim NAMES + the read shape live here.

using System.Security.Claims;

namespace AgentOs.SharedKernel.Identity;

/// <summary>Canonical claim type + role names AgentOS.NET issues (Keycloak realm mapping).</summary>
public static class ClaimNames
{
    /// <summary>Custom claim carrying the user's tenant id.</summary>
    public const string Tenant = "tenant";

    /// <summary>OIDC subject claim (the stable user id).</summary>
    public const string Subject = "sub";

    /// <summary>Realm role granting tenant-admin rights.</summary>
    public const string AdminRole = "admin";

    /// <summary>Realm role for platform-wide settings (applies to every tenant).</summary>
    public const string PlatformAdminRole = "platform-admin";

    /// <summary>Realm role for an ordinary tenant member.</summary>
    public const string MemberRole = "member";

    /// <summary>Realm role that may approve or reject the steps an approval policy holds (plan, diff, pull request,
    /// merge — E1.5). A tenant admin may approve too.</summary>
    public const string ApproverRole = "approver";
}

/// <summary>Reads AgentOS.NET identity off a <see cref="ClaimsPrincipal"/>. The single source for the
/// <c>tenant</c>/<c>sub</c>/<c>admin</c> encoding — callers supply their own fallback for a missing tenant.</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>The <c>tenant</c> claim when present and non-empty; otherwise <c>null</c> (the caller picks
    /// the fallback — <see cref="ITenantContext.DefaultTenantId"/>, empty, etc.).</summary>
    public static string? GetTenantId(this ClaimsPrincipal user)
        => user.FindFirst(ClaimNames.Tenant)?.Value is { Length: > 0 } tenant ? tenant : null;

    /// <summary>The stable user id: the <c>sub</c> claim, falling back to the remapped
    /// <see cref="ClaimTypes.NameIdentifier"/>; <c>null</c> when anonymous.</summary>
    public static string? GetUserId(this ClaimsPrincipal user)
        => user.FindFirst(ClaimNames.Subject)?.Value
           ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    /// <summary>True when the user holds the <c>admin</c> realm role.</summary>
    public static bool IsAdmin(this ClaimsPrincipal user)
        => user.IsInRole(ClaimNames.AdminRole);

    /// <summary>True when the user holds the <c>platform-admin</c> realm role — may change platform-wide
    /// settings that apply to every tenant (safety switches, sandbox, offline fallback). Distinct from a
    /// tenant <c>admin</c>, who only governs their own tenant.</summary>
    public static bool IsPlatformAdmin(this ClaimsPrincipal user)
        => user.IsInRole(ClaimNames.PlatformAdminRole);

    /// <summary>True when the user may approve or reject a step held by an approval policy: the <c>approver</c> or the
    /// <c>admin</c> realm role (E1.5).</summary>
    public static bool CanApprove(this ClaimsPrincipal user)
        => user.IsInRole(ClaimNames.ApproverRole) || user.IsInRole(ClaimNames.AdminRole);
}

/// <summary>GAP-17 — a signed-in principal without a <c>tenant</c> claim is a misconfigured token, never "the shared
/// default tenant": tenant-scoped surfaces refuse it (a 403 on a route, this message in a desktop app) instead of falling
/// back to <see cref="ITenantContext.DefaultTenantId"/>.</summary>
public static class TenantClaim
{
    /// <summary>What a desktop app shows when the sign-in has no tenant.</summary>
    public const string MissingMessage =
        "Your sign-in doesn't name a workspace, so nothing is shown. Sign out and in again; if it persists, ask your administrator to check your account's tenant.";
}
