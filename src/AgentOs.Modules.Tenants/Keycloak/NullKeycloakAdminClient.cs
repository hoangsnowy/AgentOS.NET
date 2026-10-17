// Dev / CI fallback for IKeycloakAdminClient, used when no Keycloak server is configured
// (Auth:Keycloak:Admin:BaseUrl empty — e.g. standalone Web with DevAutoLogin, or CI). The real
// KeycloakAdminClient throws on the first call because its HttpClient has no BaseAddress; that
// crashed the Users desktop app's circuit in dev. This impl degrades every operation to a safe
// no-op / empty result so the app boots and renders an empty member list instead of throwing.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Modules.Tenants.Keycloak;

/// <summary>No-op <see cref="IKeycloakAdminClient"/> for environments without Keycloak. Reads
/// return empty; writes complete without effect. Never throws.</summary>
public sealed class NullKeycloakAdminClient : IKeycloakAdminClient
{
    /// <summary>A stable, obviously-fake user id so callers that persist the result stay coherent.</summary>
    private const string DevUserId = "dev-user";

    public Task<string> CreateUserAsync(
        string username,
        string email,
        string tenantId,
        IReadOnlyList<string> realmRoles,
        bool sendVerifyEmail,
        string? password = null,
        CancellationToken ct = default)
        => Task.FromResult(DevUserId);

    public Task DeleteUserAsync(string userId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<KeycloakUser>> ListUsersByTenantAsync(string tenantId, int max = 200, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<KeycloakUser>>(Array.Empty<KeycloakUser>());

    public Task UpdateUserRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SendPasswordResetEmailAsync(string userId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string?> GetServerVersionAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    // No Keycloak ⇒ no users to confirm; null means the cross-tenant guard treats every target as
    // not-in-tenant (the member-admin endpoints aren't reachable in a no-Keycloak standalone anyway).
    public Task<string?> GetUserTenantAsync(string userId, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
