// A tenant API token row (E1.3). Only a salted HMAC of the token is stored; Prefix is the lookup key.

using System;

namespace AgentOs.Modules.Tenants.ApiTokens;

/// <summary>tenants.api_tokens.</summary>
public sealed class ApiTokenEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256(key = <see cref="Salt"/>, token) as lower hex; plain SHA-256 for rows created before the salt.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Per-token random salt (hex). Empty only on legacy rows (unsalted SHA-256).</summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary><see cref="ApiTokenKind.Personal"/> (acts as its owner) or <see cref="ApiTokenKind.Service"/> (a service account).</summary>
    public string Kind { get; set; } = ApiTokenKind.Service;

    /// <summary>Space-separated scopes (<see cref="ApiTokenScopes"/>), closed upwards: admin ⇒ run ⇒ read.</summary>
    public string Scopes { get; set; } = ApiTokenScopes.Read;

    /// <summary>The Keycloak user id a personal token acts as; null for service accounts.</summary>
    public string? OwnerUserId { get; set; }

    /// <summary>Derived from <see cref="Scopes"/> (admin scope ⇒ admin, else member) — kept for older readers.</summary>
    public string Role { get; set; } = "member";
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
