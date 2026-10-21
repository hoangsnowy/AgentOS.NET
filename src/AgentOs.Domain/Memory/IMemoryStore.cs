// AgentOs.Domain/Memory/IMemoryStore.cs
// The shared knowledge graph (ADR-0008), slice 1: what the agents learned on a project, as nodes and typed edges.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Memory;

/// <summary>Node kinds of the knowledge graph (slice 1 writes these; ADR-0008 lists the rest).</summary>
public static class MemoryKinds
{
    public const string Repo = "Repo";
    public const string Ticket = "Ticket";
    public const string PullRequest = "PullRequest";
    /// <summary>Something the agents must not repeat: a review comment, a CI failure and its cause, an acceptance finding.</summary>
    public const string Lesson = "Lesson";
}

/// <summary>Edge relations.</summary>
public static class MemoryRels
{
    public const string InRepo = "in_repo";
    public const string SolvedBy = "solved_by";
    public const string DependsOn = "depends_on";
    public const string LearnedFrom = "learned_from";
}

/// <summary>A node to record. <paramref name="Key"/> is its natural key per tenant (e.g. <c>ticket:owner/repo#7</c>) —
/// recording the same key again updates it instead of adding a duplicate.</summary>
public sealed record MemoryNodeInput(string Kind, string Key, string Title, string? Body = null, string? RepoKey = null, string? SourceRef = null);

/// <summary>A recorded node as the desktop and the agents read it.</summary>
public sealed record MemoryNode(Guid Id, string Kind, string Key, string Title, string? Body, string? RepoKey, bool Pinned,
    DateTimeOffset RecordedAt, string? SourceRef);

/// <summary>A live (not invalidated) edge.</summary>
public sealed record MemoryEdge(Guid From, Guid To, string Rel);

/// <summary>A slice of the graph for the visualiser.</summary>
public sealed record MemoryGraph(IReadOnlyList<MemoryNode> Nodes, IReadOnlyList<MemoryEdge> Edges);

/// <summary>
/// Tenant-explicit knowledge-graph store. Facts are never overwritten in place: a forgotten node is retracted (kept,
/// with its retraction time) so the graph can be read as of any date. Every method is per tenant; nothing reads
/// across tenants.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Record (or refresh) a node; returns its id.</summary>
    Task<Guid> RecordAsync(string tenantId, MemoryNodeInput node, CancellationToken ct = default);

    /// <summary>Add a typed edge once (an identical live edge is not duplicated).</summary>
    Task LinkAsync(string tenantId, Guid fromId, Guid toId, string rel, CancellationToken ct = default);

    /// <summary>The lessons an agent should read before working on <paramref name="repoKey"/>: pinned first, then the
    /// ones closest to <paramref name="query"/> (full-text), then the most recent. Marks them used.</summary>
    Task<IReadOnlyList<MemoryNode>> RecallLessonsAsync(string tenantId, string repoKey, string query, int max, CancellationToken ct = default);

    /// <summary>The graph around a project (or the whole tenant when <paramref name="repoKey"/> is null), newest first.</summary>
    Task<MemoryGraph> GraphAsync(string tenantId, string? repoKey, int maxNodes, CancellationToken ct = default);

    /// <summary>Pin or unpin a lesson (pinned lessons are always recalled first).</summary>
    Task SetPinnedAsync(string tenantId, Guid nodeId, bool pinned, CancellationToken ct = default);

    /// <summary>Forget a node: it stops being recalled or shown (retracted, not deleted).</summary>
    Task ForgetAsync(string tenantId, Guid nodeId, CancellationToken ct = default);
}

/// <summary>The store when no database is configured (standalone desktop): remembers nothing.</summary>
public sealed class NullMemoryStore : IMemoryStore
{
    public Task<Guid> RecordAsync(string tenantId, MemoryNodeInput node, CancellationToken ct = default) => Task.FromResult(Guid.Empty);

    public Task LinkAsync(string tenantId, Guid fromId, Guid toId, string rel, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<MemoryNode>> RecallLessonsAsync(string tenantId, string repoKey, string query, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryNode>>([]);

    public Task<MemoryGraph> GraphAsync(string tenantId, string? repoKey, int maxNodes, CancellationToken ct = default) =>
        Task.FromResult(new MemoryGraph([], []));

    public Task SetPinnedAsync(string tenantId, Guid nodeId, bool pinned, CancellationToken ct = default) => Task.CompletedTask;

    public Task ForgetAsync(string tenantId, Guid nodeId, CancellationToken ct = default) => Task.CompletedTask;
}
