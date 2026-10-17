// A tenant API token row (E1.3). Only the SHA-256 of the token is stored; Prefix is the lookup key.

using System;

namespace AgentOs.Modules.Tenants.ApiTokens;

/// <summary>tenants.api_tokens.</summary>
public sealed class ApiTokenEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string Role { get; set; } = "member";
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
