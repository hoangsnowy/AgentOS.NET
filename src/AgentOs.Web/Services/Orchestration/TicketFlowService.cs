// The ticket flow a board runs (ADR-0016): the board's own flow when it has one, else the tenant's, else the built-in
// one (Code → Checks → Review → Test → Merge). Every load is validated: a stored version that breaks a safety
// invariant is refused — the run uses the built-in flow and the reason is returned so Autopilot's log can say so.
// Saving validates first (a definition that breaks an invariant is refused with the reasons, nothing is stored), only
// an admin may save, and every change appends a version (who / when), so any earlier one can be brought back.
// Versions are immutable, so a parsed version is cached per (tenant, version) — the Autopilot pass reads one per task.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Workspaces.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>Where the ticket flow a board runs comes from.</summary>
public enum TicketFlowSource
{
    /// <summary>The built-in flow (nothing saved, or what was saved was refused).</summary>
    BuiltIn,

    /// <summary>The tenant's flow (every project without its own).</summary>
    Tenant,

    /// <summary>This project's own flow.</summary>
    Board,
}

/// <summary>The flow a board runs now.</summary>
/// <param name="Definition">The definition (always safe to run).</param>
/// <param name="Version">Its version (0 = the built-in flow).</param>
/// <param name="Source">Where it comes from.</param>
/// <param name="Refused">Why a stored flow was refused in favour of the built-in one (plain words), or null.</param>
public sealed record EffectiveTicketFlow(TicketFlowDefinition Definition, int Version, TicketFlowSource Source, string? Refused = null)
{
    /// <summary>The built-in flow.</summary>
    public static EffectiveTicketFlow BuiltIn { get; } = new(TicketFlowDefinition.Default, 0, TicketFlowSource.BuiltIn);
}

/// <summary>Who changes a ticket flow.</summary>
public sealed record TicketFlowActor(string? UserId, string? Name, bool IsAdmin);

/// <summary>The outcome of a save or a revert.</summary>
/// <param name="Version">The new version, when saved.</param>
/// <param name="Problems">Why it was refused (empty when saved).</param>
public sealed record TicketFlowSaveResult(TicketFlowVersion? Version, IReadOnlyList<TicketFlowProblem> Problems)
{
    /// <summary>True when a version was appended.</summary>
    public bool Saved => Version is not null;

    /// <summary>The reasons in one sentence each.</summary>
    public string Reason => string.Join(" ", Problems.Select(p => p.Message));
}

/// <summary>Reads, validates, saves and reverts ticket flows. Singleton; tenant-explicit.</summary>
public sealed class TicketFlowService(IServiceScopeFactory scopes, ILogger<TicketFlowService> logger)
{
    private const int CacheCap = 2048;
    private readonly ConcurrentDictionary<(string Tenant, int Version), (TicketFlowDefinition? Definition, Guid BoardId)> _parsed = new();

    /// <summary>Raised (with the tenant) after a flow of that tenant changed, so open views refresh.</summary>
    public event Action<string>? Changed;

    /// <summary>The flow <paramref name="boardId"/> runs now (null / <see cref="Guid.Empty"/> = the tenant's flow).</summary>
    public async Task<EffectiveTicketFlow> ResolveAsync(string tenantId, Guid? boardId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITicketFlowStore>();
        if (boardId is { } board && board != Guid.Empty
            && await store.CurrentAsync(tenantId, board, ct).ConfigureAwait(false) is { DefinitionJson: not null } own)
        {
            return Load(tenantId, own, TicketFlowSource.Board);
        }
        return await store.CurrentAsync(tenantId, Guid.Empty, ct).ConfigureAwait(false) is { DefinitionJson: not null } tenant
            ? Load(tenantId, tenant, TicketFlowSource.Tenant)
            : EffectiveTicketFlow.BuiltIn;
    }

    /// <summary>The flow a task runs: the version it started with (<paramref name="snapshot"/>; 0 = built-in), or — for a
    /// task started before ticket flows — what the board runs now.</summary>
    public async Task<EffectiveTicketFlow> ForTaskAsync(string tenantId, Guid boardId, int? snapshot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (snapshot is null) { return await ResolveAsync(tenantId, boardId, ct).ConfigureAwait(false); }
        if (snapshot.Value <= 0) { return EffectiveTicketFlow.BuiltIn; }
        if (_parsed.TryGetValue((tenantId, snapshot.Value), out var cached))
        {
            return cached.Definition is null
                ? Refuse(snapshot.Value, "it breaks a safety rule")
                : new(cached.Definition, snapshot.Value, cached.BoardId == Guid.Empty ? TicketFlowSource.Tenant : TicketFlowSource.Board);
        }
        using var scope = scopes.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<ITicketFlowStore>().GetVersionAsync(tenantId, snapshot.Value, ct).ConfigureAwait(false);
        return row is { DefinitionJson: not null }
            ? Load(tenantId, row, row.BoardId == Guid.Empty ? TicketFlowSource.Tenant : TicketFlowSource.Board)
            : Refuse(snapshot.Value, "it no longer exists");
    }

    /// <summary>The versions saved for a scope (a board, or null for the tenant), newest first.</summary>
    public async Task<IReadOnlyList<TicketFlowVersion>> HistoryAsync(string tenantId, Guid? boardId, int max = 20, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITicketFlowStore>()
            .HistoryAsync(tenantId, boardId ?? Guid.Empty, max, ct).ConfigureAwait(false);
    }

    /// <summary>Save <paramref name="definition"/> as the scope's flow — refused, with the reasons, when it breaks an
    /// invariant or the actor isn't an admin.</summary>
    public async Task<TicketFlowSaveResult> SaveAsync(string tenantId, Guid? boardId, TicketFlowDefinition definition, TicketFlowActor actor,
        string? note = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAdmin) { return new(null, [new(null, "Only an admin can change the ticket flow.")]); }
        var problems = TicketFlowValidator.Validate(definition);
        if (problems.Count > 0) { return new(null, problems); }
        return await AppendAsync(tenantId, boardId, definition.ToJson(), actor, note, ct).ConfigureAwait(false);
    }

    /// <summary>Bring version <paramref name="version"/> of the scope back (as a new version, so the history stays whole).</summary>
    public async Task<TicketFlowSaveResult> RevertAsync(string tenantId, Guid? boardId, int version, TicketFlowActor actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAdmin) { return new(null, [new(null, "Only an admin can change the ticket flow.")]); }
        TicketFlowVersion? row;
        using (var scope = scopes.CreateScope())
        {
            row = await scope.ServiceProvider.GetRequiredService<ITicketFlowStore>().GetVersionAsync(tenantId, version, ct).ConfigureAwait(false);
        }
        var scopeId = boardId ?? Guid.Empty;
        if (row is null || row.BoardId != scopeId) { return new(null, [new(null, $"Version {version} isn't one of this flow's versions.")]); }
        var note = $"Reverted to v{version}";
        if (row.DefinitionJson is null) { return await AppendAsync(tenantId, boardId, null, actor, note, ct).ConfigureAwait(false); }
        var definition = TicketFlowDefinition.FromJson(row.DefinitionJson);
        var problems = TicketFlowValidator.Validate(definition);
        if (problems.Count > 0) { return new(null, problems); }
        return await AppendAsync(tenantId, boardId, definition!.ToJson(), actor, note, ct).ConfigureAwait(false);
    }

    /// <summary>The board drops its own flow and runs the tenant's again.</summary>
    public Task<TicketFlowSaveResult> UseTenantFlowAsync(string tenantId, Guid boardId, TicketFlowActor actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAdmin) { return Task.FromResult(new TicketFlowSaveResult(null, [new(null, "Only an admin can change the ticket flow.")])); }
        return boardId == Guid.Empty
            ? Task.FromResult(new TicketFlowSaveResult(null, [new(null, "Pick a project first.")]))
            : AppendAsync(tenantId, boardId, null, actor, "Uses the default flow again", ct);
    }

    private async Task<TicketFlowSaveResult> AppendAsync(string tenantId, Guid? boardId, string? json, TicketFlowActor actor, string? note, CancellationToken ct)
    {
        TicketFlowVersion saved;
        using (var scope = scopes.CreateScope())
        {
            saved = await scope.ServiceProvider.GetRequiredService<ITicketFlowStore>()
                .AppendAsync(tenantId, boardId ?? Guid.Empty, json, actor.UserId, actor.Name, note, ct).ConfigureAwait(false);
            await AuditAsync(scope.ServiceProvider, tenantId, actor, saved, ct).ConfigureAwait(false);
        }
        try
        {
            Changed?.Invoke(tenantId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A ticket flow listener failed");
        }
        return new(saved, []);
    }

    // Governance: the change is in Settings › Activity log like any other tenant setting change. Best-effort.
    private static async Task AuditAsync(IServiceProvider sp, string tenantId, TicketFlowActor actor, TicketFlowVersion saved, CancellationToken ct)
    {
        if (sp.GetService<AgentOs.Modules.Tenants.IAuditLog>() is not { } audit) { return; }
        var target = (saved.BoardId == Guid.Empty ? "ticket flow (all projects)" : $"ticket flow (project {saved.BoardId:D})") + $" v{saved.Version}"
            + (saved.Note is null ? "" : $" — {saved.Note}");
        await audit.WriteAsync(new AgentOs.Modules.Tenants.AuditEntry(Guid.NewGuid(), tenantId, actor.UserId,
            AgentOs.Modules.Tenants.AuditActions.TicketFlowChanged, target, null, null, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
    }

    private EffectiveTicketFlow Load(string tenantId, TicketFlowVersion row, TicketFlowSource source)
    {
        if (_parsed.Count > CacheCap) { _parsed.Clear(); }
        var (definition, _) = _parsed.GetOrAdd((tenantId, row.Version), _ => (Parse(row.DefinitionJson), row.BoardId));
        return definition is null ? Refuse(row.Version, ReasonFor(row.DefinitionJson)) : new(definition, row.Version, source);
    }

    // Parsed AND valid, or null: a cached entry is always safe to run.
    private static TicketFlowDefinition? Parse(string? json)
    {
        var definition = TicketFlowDefinition.FromJson(json);
        return TicketFlowValidator.IsValid(definition) ? definition : null;
    }

    private static string ReasonFor(string? json) =>
        TicketFlowValidator.Validate(TicketFlowDefinition.FromJson(json)) is { Count: > 0 } problems ? problems[0].Message.TrimEnd('.') : "it can't be read";

    private EffectiveTicketFlow Refuse(int version, string why)
    {
        logger.LogWarning("Ticket flow v{Version} refused; the built-in flow runs instead", version);
        return EffectiveTicketFlow.BuiltIn with { Refused = $"Ticket flow v{version} was refused ({why}) — the built-in flow runs instead." };
    }
}
