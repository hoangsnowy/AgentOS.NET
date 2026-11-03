// Who can approve in a tenant (E1.5): the enabled members holding the approver or admin realm role, counted through the
// Keycloak admin API. Approval gates fail closed on a known zero ("nobody can approve" stops the step instead of letting
// it through); without a Keycloak admin API (the standalone desktop, CI) the count is unknown — the gate still asks and
// waits, it never goes ahead. The count is cached per tenant for a minute (a pass reads it for every held task), and a
// failed read is not cached.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Governance;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Tenants.Approvals;

/// <summary><see cref="IApproverDirectory"/> over the Keycloak admin API. A singleton (it holds the cache); the admin client
/// is a typed HttpClient, so it is resolved per call from a scope rather than captured.</summary>
internal sealed class KeycloakApproverDirectory(IServiceScopeFactory scopes, TimeProvider clock, ILogger<KeycloakApproverDirectory> logger) : IApproverDirectory
{
    internal static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, int Count)> _cache = new(StringComparer.Ordinal);

    public async Task<int?> CountApproversAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) { return null; }
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(tenantId, out var cached) && now - cached.At < CacheFor) { return cached.Count; }
        using var scope = scopes.CreateScope();
        var keycloak = scope.ServiceProvider.GetRequiredService<IKeycloakAdminClient>();
        if (keycloak is NullKeycloakAdminClient) { return null; }
        try
        {
            var users = await keycloak.ListUsersByTenantAsync(tenantId, ct: ct).ConfigureAwait(false);
            var count = users.Count(u => u.Enabled && PermissionMatrix.For(u.Roles).Has(Permission.ApproveWork));
            _cache[tenantId] = (now, count);
            return count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't count the approvers of tenant {Tenant}; approval gates wait instead", tenantId);
            return null;
        }
    }
}
