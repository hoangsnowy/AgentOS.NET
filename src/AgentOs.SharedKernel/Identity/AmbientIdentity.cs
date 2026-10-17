// Ambient (tenant, user) identity for code paths that run with no HttpContext — chiefly a Blazor
// Server circuit's fire-and-forget Task.Run, where the request-scoped ITenantContext (HttpTenantContext)
// resolves blank (it returns DefaultTenantId / null because there is no HttpContext.User). Set it around
// the unit of work; ITenantContext-blind consumers — the LLM tool tenant + the runner dispatch target —
// read it as an override of last resort. Backed by AsyncLocal, so it flows across awaits within the same
// logical call and stays isolated between concurrent flows (each Task.Run gets its own branch).
//
// This complements the codebase's "tenant-explicit overload" pattern (repos take a tenantId): it carries
// identity into singletons (RunnerShellTool) and provider-neutral seams (ILlmClient) that cannot take a
// per-call tenant argument, without resurrecting a mutable scoped ITenantContext.

using System;

namespace AgentOs.SharedKernel.Identity;

/// <summary>An ambient (tenant, user) identity for background work that has no request/HttpContext.</summary>
public static class AmbientIdentity
{
    private static readonly AsyncLocal<Identity?> _current = new();

    /// <summary>The identity in force for the current async flow, or <c>null</c> when none is set.</summary>
    public static Identity? Current => _current.Value;

    /// <summary>
    /// Set the ambient identity for the current async flow. Dispose the returned handle to restore the
    /// previous value — wrap the unit of work in <c>using var _ = AmbientIdentity.Push(tenant, user);</c>.
    /// </summary>
    public static IDisposable Push(string tenantId, string? userId) => Push(tenantId, userId, null);

    /// <summary>
    /// Push only when <paramref name="tenantId"/> is non-blank; returns <c>null</c> otherwise (standalone
    /// dev-login carries no tenant claim). Lets every off-circuit run site write a single
    /// <c>using var _ = AmbientIdentity.PushOrNull(tenantId, userId);</c> instead of hand-rolling the
    /// <c>IsNullOrEmpty(tenantId) ? null : Push(...)</c> guard — which, when one path forgot it, silently
    /// resolved that path's work (LLM key, budget, evidence) under the <c>default</c> tenant.
    /// </summary>
    public static IDisposable? PushOrNull(string? tenantId, string? userId)
        => string.IsNullOrWhiteSpace(tenantId) ? null : Push(tenantId, userId, null);

    /// <summary>
    /// The ONE tenant/user precedence every off-circuit run path shares: an explicit argument (if non-blank)
    /// wins, then the <see cref="Current">ambient</see> identity, then the request-scoped
    /// <paramref name="context"/>, then <see cref="ITenantContext.DefaultTenantId"/>. The context's value is
    /// used verbatim when present (an authenticated-but-empty tenant stays empty — fail-closed — rather than
    /// silently becoming <c>default</c>); only a missing context falls through to the default. Pure read — the
    /// caller pushes the result (e.g. <c>using var _ = AmbientIdentity.Push(id.TenantId, id.UserId);</c>),
    /// because some callers resolve on the live request thread but push later on a Task.Run branch.
    /// </summary>
    public static Identity Resolve(string? explicitTenantId, string? explicitUserId, ITenantContext? context)
    {
        var tenant =
            (string.IsNullOrWhiteSpace(explicitTenantId) ? null : explicitTenantId)
            ?? Current?.TenantId
            ?? context?.TenantId
            ?? ITenantContext.DefaultTenantId;
        var user = explicitUserId ?? Current?.UserId ?? context?.UserId;
        return new Identity(tenant, user);
    }

    /// <summary>
    /// Set the ambient identity including the running session, so off-box tools (runner_shell) can tag
    /// their per-command progress events back to the originating session without threading a session id
    /// through the LLM request → tool-gateway hot path.
    /// </summary>
    public static IDisposable Push(string tenantId, string? userId, Guid? sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var previous = _current.Value;
        _current.Value = new Identity(tenantId, userId, sessionId);
        return new Scope(previous);
    }

    /// <summary>Set the ambient identity of a signed-in user, with a display name for audit trails.</summary>
    public static IDisposable PushUser(string tenantId, string? userId, string? userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var previous = _current.Value;
        _current.Value = new Identity(tenantId, userId) { UserName = userName };
        return new Scope(previous);
    }

    /// <summary>An ambient (tenant, user, session) tuple.</summary>
    /// <param name="TenantId">The tenant the work runs under. Never empty.</param>
    /// <param name="UserId">The member the work belongs to (token <c>sub</c>), or null.</param>
    /// <param name="SessionId">The running session, when the work belongs to one; else null.</param>
    public sealed record Identity(string TenantId, string? UserId, Guid? SessionId = null)
    {
        /// <summary>The user's display name (preferred username), when known — for audit "who" columns.</summary>
        public string? UserName { get; init; }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Identity? _previous;
        private bool _disposed;

        public Scope(Identity? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _current.Value = _previous;
        }
    }
}
