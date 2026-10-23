// After a ticket merges, the Retrospective agent turns the project's raw lessons into its practices (see
// IRetrospectiveAgent). Runs in the background, at most once per project every 10 minutes, one at a time per project.
// Guardrails: pinned practices and pinned lessons are never removed; only lessons the agent was shown can be cleared;
// an empty or failed retrospective changes nothing. The run is recorded in Insights and summarised in the Autopilot log.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Memory;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Web.Services.BoardTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>Runs project retrospectives after merges. Singleton.</summary>
public sealed class RetrospectiveService(IServiceScopeFactory scopes, BoardTaskService tasks, OrchestratorLog log, ILogger<RetrospectiveService> logger)
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
    private const int MaxLessons = 40;

    private readonly ConcurrentDictionary<(string Tenant, string Repo), DateTimeOffset> _last = new();
    private readonly ConcurrentDictionary<(string Tenant, string Repo), SemaphoreSlim> _locks = new();

    /// <summary>Start a retrospective for <paramref name="repoKey"/> after <paramref name="ticket"/> merged (fire and forget).</summary>
    public void AfterMerge(string tenantId, string memberId, Guid boardId, string repoKey, string ticket)
    {
        var key = (tenantId, repoKey);
        if (_last.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last < MinInterval) { return; }
        _last[key] = DateTimeOffset.UtcNow;
        _ = Task.Run(() => RunAsync(tenantId, memberId, boardId, repoKey, ticket, CancellationToken.None));
    }

    /// <summary>Run one retrospective now. Returns what changed (for tests and the log); never throws.</summary>
    public async Task<RetrospectiveOutcome> RunAsync(string tenantId, string memberId, Guid boardId, string repoKey, string ticket, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd((tenantId, repoKey), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var identity = AgentOs.SharedKernel.Identity.AmbientIdentity.Push(tenantId, memberId);
            using var scope = scopes.CreateScope();
            var memory = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
            var lessons = await memory.ListAsync(tenantId, repoKey, MemoryKinds.Lesson, MaxLessons, ct).ConfigureAwait(false);
            if (lessons.Count == 0) { return RetrospectiveOutcome.None; }
            var practices = await memory.ListAsync(tenantId, repoKey, MemoryKinds.Practice, RetrospectiveAgent.MaxPractices * 2, ct).ConfigureAwait(false);

            var lessonIds = lessons.Select((l, i) => (Id: RetrospectiveAgent.ShortId('L', i), Node: l)).ToList();
            var input = new RetrospectiveInput(tenantId, memberId, repoKey, ticket,
                lessonIds.Select(l => new RetrospectiveItem(l.Id, l.Node.Body ?? l.Node.Title, l.Node.Pinned)).ToList(),
                practices.Select((p, i) => new RetrospectiveItem(RetrospectiveAgent.ShortId('P', i), p.Body ?? p.Title, p.Pinned)).ToList());

            RetrospectiveResult result;
            var started = DateTimeOffset.UtcNow;
            using (var calls = AgentOs.Domain.Llm.LlmCallScope.Begin())
            {
                result = await scope.ServiceProvider.GetRequiredService<IRetrospectiveAgent>().RunAsync(input, ct).ConfigureAwait(false);
                await tasks.SaveAgentRunAsync(tenantId, memberId, "Retrospective", $"Retrospective · {repoKey}", started, calls.Calls).ConfigureAwait(false);
            }
            if (!result.Ran) { return RetrospectiveOutcome.None; }

            var outcome = await ApplyAsync(memory, tenantId, repoKey, result, practices, lessonIds.Select(l => (l.Id, l.Node)).ToList(), ct).ConfigureAwait(false);
            log.Add(tenantId, boardId,
                $"Retrospective on {repoKey}: {(string.IsNullOrWhiteSpace(result.Summary) ? "practices updated" : result.Summary)} — "
                + $"{outcome.Practices} practice(s), {outcome.Added} new, {outcome.ClearedLessons} lesson(s) folded in.");
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Retrospective failed for {Repo}", repoKey);
            return RetrospectiveOutcome.None;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Write the new practices, retire the old unpinned ones they replace, clear the absorbed unpinned lessons.</summary>
    internal static async Task<RetrospectiveOutcome> ApplyAsync(IMemoryStore memory, string tenantId, string repoKey, RetrospectiveResult result,
        IReadOnlyList<MemoryNode> oldPractices, IReadOnlyList<(string Id, MemoryNode Node)> lessons, CancellationToken ct)
    {
        static string Norm(string s) => s.Trim().ToLowerInvariant();
        var kept = result.Practices.Select(Norm).ToHashSet(StringComparer.Ordinal);
        var existing = oldPractices.Select(p => Norm(p.Body ?? p.Title)).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var practice in result.Practices)
        {
            await ProjectMemory.RecordPracticeAsync(memory, tenantId, repoKey, practice, ct).ConfigureAwait(false);
            if (!existing.Contains(Norm(practice))) { added++; }
        }
        foreach (var old in oldPractices.Where(p => !p.Pinned && !kept.Contains(Norm(p.Body ?? p.Title))))
        {
            await memory.ForgetAsync(tenantId, old.Id, ct).ConfigureAwait(false);
        }
        var absorbed = result.AbsorbedLessonIds.ToHashSet(StringComparer.Ordinal);
        var cleared = 0;
        foreach (var (id, node) in lessons.Where(l => absorbed.Contains(l.Id) && !l.Node.Pinned))
        {
            await memory.ForgetAsync(tenantId, node.Id, ct).ConfigureAwait(false);
            cleared++;
        }
        return new RetrospectiveOutcome(result.Practices.Count, added, cleared);
    }
}

/// <summary>What a retrospective changed.</summary>
public sealed record RetrospectiveOutcome(int Practices, int Added, int ClearedLessons)
{
    public static RetrospectiveOutcome None { get; } = new(0, 0, 0);
}
