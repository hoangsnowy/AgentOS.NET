// CreateUserAsync is several Keycloak calls (POST /users, then role grants and the actions email). When a step after
// the POST fails, the client deletes the account it just made so that "CreateUserAsync threw" keeps meaning "no account
// exists" — the sign-up saga relies on that to give an invitation back. This exception is what it throws when that
// compensating delete itself failed: the account is still there, and the caller must NOT treat the failure as a clean
// refusal (GAP-11: one invitation ⇔ at most one audited account).

using System;

namespace AgentOs.Modules.Tenants.Keycloak;

/// <summary>Creating a Keycloak account failed after the account existed, and removing it failed too: a user with the
/// given id is left behind in the realm.</summary>
public sealed class KeycloakUserLeftBehindException : InvalidOperationException
{
    /// <summary>The Keycloak id of the account that still exists.</summary>
    public string? UserId { get; init; }

    /// <inheritdoc />
    public KeycloakUserLeftBehindException() { }

    /// <inheritdoc />
    public KeycloakUserLeftBehindException(string message) : base(message) { }

    /// <inheritdoc />
    public KeycloakUserLeftBehindException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Names the account that could not be removed; <paramref name="cause"/> is the failure that made the
    /// creation abort.</summary>
    public static KeycloakUserLeftBehindException For(string userId, Exception cause) =>
        new($"Keycloak user '{userId}' was created, the remaining set-up failed, and the account could not be removed.", cause)
        {
            UserId = userId,
        };
}
