// Thin abstraction over the Keycloak Admin REST API: enough to provision a tenant's admin user
// and to invite additional members. Implemented as a typed HttpClient.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Modules.Tenants.Keycloak;

/// <summary>Provisioning operations against a Keycloak realm.</summary>
public interface IKeycloakAdminClient
{
    /// <summary>Create a user with username + email + a single <c>tenant</c> attribute, grant the
    /// listed realm roles, and optionally seed a password or trigger Keycloak's reset-password
    /// email so the invitee picks the initial password themselves. Returns the new Keycloak user id.</summary>
    Task<string> CreateUserAsync(
        string username,
        string email,
        string tenantId,
        IReadOnlyList<string> realmRoles,
        bool sendVerifyEmail,
        string? password = null,
        CancellationToken ct = default);

    /// <summary>Delete a Keycloak user by id. Used by the signup saga to roll back a half-created
    /// user when the downstream registry write throws, and by tenant-admin member management to
    /// remove a member. Throws on any non-success response (404 is treated as success — already gone).</summary>
    Task DeleteUserAsync(string userId, CancellationToken ct = default);

    /// <summary>List realm users whose <c>tenant</c> attribute matches the given id. Client-side
    /// filter over a paged GET; bounded by <paramref name="max"/>. Realms larger than the bound
    /// need a server-side search rewrite — call out the limit in the response if you display it.</summary>
    Task<IReadOnlyList<KeycloakUser>> ListUsersByTenantAsync(string tenantId, int max = 200, CancellationToken ct = default);

    /// <summary>Set a member's realm roles to exactly <paramref name="roles"/> (managed set:
    /// <c>admin</c> + <c>member</c>). Diffs against the user's current mappings — grants the missing
    /// ones, revokes the managed ones no longer desired, leaves unrelated roles untouched. Throws
    /// <see cref="System.InvalidOperationException"/> on a non-success response.</summary>
    Task UpdateUserRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken ct = default);

    /// <summary>Trigger Keycloak's <c>UPDATE_PASSWORD</c> action email so the member resets their own
    /// password (the server never exposes the old one). Best-effort: logs + throws on transport
    /// failure so the caller can surface it.</summary>
    Task SendPasswordResetEmailAsync(string userId, CancellationToken ct = default);

    /// <summary>Return the value of a user's single <c>tenant</c> attribute, or <c>null</c> if the user
    /// doesn't exist or has no tenant attribute. Used to verify a target user belongs to the caller's
    /// tenant BEFORE a cross-tenant-capable admin operation — the realm <c>admin</c> role is realm-wide,
    /// so the route tenant alone is not sufficient authorization for acting on an arbitrary user id.</summary>
    Task<string?> GetUserTenantAsync(string userId, CancellationToken ct = default);

    /// <summary>The Keycloak server version (admin <c>serverinfo</c>, needs the master-realm admin the client
    /// already signs in as), or <c>null</c> when no Keycloak is configured. Shown on System → About.</summary>
    Task<string?> GetServerVersionAsync(CancellationToken ct = default);
}

/// <summary>Realm-user projection returned by the admin REST list endpoint.</summary>
public sealed record KeycloakUser(
    string Id,
    string Username,
    string? Email,
    bool Enabled,
    bool EmailVerified,
    System.Collections.Generic.IReadOnlyList<string> Roles);

