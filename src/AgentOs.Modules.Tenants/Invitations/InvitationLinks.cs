// GAP-05 / GAP-11 — the one place an invitation link is built, for the Api (POST /tenants/{id}/invitations) and the
// desktop (Settings › Users). The link carries a single-use token, so it must point at the configured public desktop
// (Hosting:PublicBaseUrl) — never at the Host header of the request, or the base URI of the Blazor circuit, that minted
// it: both are client input, and a poisoned origin would hand the token to someone else's server. Only in Development
// (no configured URL) does the caller's own origin stand in.

using System;
using AgentOs.SharedKernel.Hosting;

namespace AgentOs.Modules.Tenants.Invitations;

/// <summary>Builds invitation links from the public origin.</summary>
public static class InvitationLinks
{
    /// <summary>The sign-up path that accepts <paramref name="token"/> (relative to the desktop).</summary>
    public static string PathFor(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return $"/signup?invite={Uri.EscapeDataString(token)}";
    }

    /// <summary>The absolute link for <paramref name="token"/>: under <see cref="PublicOrigin.Configured"/>, or — in
    /// Development only — under <paramref name="callerOrigin"/> (the request's origin or the circuit's base URI).
    /// Throws when neither applies.</summary>
    public static string Absolute(PublicOrigin origin, string token, string? callerOrigin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return origin.BaseUrl(callerOrigin) + PathFor(token);
    }
}
