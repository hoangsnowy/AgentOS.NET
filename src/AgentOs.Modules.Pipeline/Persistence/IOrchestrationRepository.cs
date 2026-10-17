// Repository for the AgentOS.NET orchestration graph. Stored as a JSON string (the Web layer
// (de)serializes it to its own OrchestrationGraph) → Application/Infrastructure do not depend on Web.
namespace AgentOs.Modules.Pipeline.Persistence;

/// <summary>CRUD for the orchestration definition (AgentOS.NET orchestration editor state).</summary>
public interface IOrchestrationRepository
{
    Task<IReadOnlyList<OrchestrationRecord>> ListAsync(CancellationToken ct = default);

    Task<OrchestrationRecord?> GetAsync(string id, CancellationToken ct = default);

    Task UpsertAsync(OrchestrationRecord record, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    // Tenant-EXPLICIT overloads. A Blazor circuit dispatches persistence on a Task.Run threadpool thread
    // that has no HttpContext, so the ambient ITenantContext would resolve to the default tenant — these
    // take the tenant from the signed-in principal instead, keeping orchestrations isolated per tenant.
    Task<IReadOnlyList<OrchestrationRecord>> ListForTenantAsync(string tenantId, CancellationToken ct = default);

    Task UpsertForTenantAsync(string tenantId, OrchestrationRecord record, CancellationToken ct = default);

    Task DeleteForTenantAsync(string tenantId, string id, CancellationToken ct = default);
}

/// <summary>A single orchestration graph (DefinitionJson = full graph serialized by the Web layer).</summary>
public sealed record OrchestrationRecord(
    string Id,
    string Name,
    string? Description,
    string DefinitionJson,
    DateTimeOffset UpdatedAtUtc);
