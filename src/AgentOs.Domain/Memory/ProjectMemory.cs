// AgentOs.Domain/Memory/ProjectMemory.cs
// What the three agentic phases write into the knowledge graph, and how lessons are read back into a prompt.
// Best-effort by design: memory must never fail or slow down the work it describes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;

namespace AgentOs.Domain.Memory;

/// <summary>Knowledge-graph writes and reads for projects (repos).</summary>
public static class ProjectMemory
{
    /// <summary>A proposed change was opened for a ticket: Repo ← Ticket → PullRequest.</summary>
    public static async Task RecordChangeAsync(IMemoryStore store, string tenantId, string owner, string repo, int? issueNumber,
        string ticketTitle, string prUrl, string prTitle, string? summary, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            var repoKey = $"{owner}/{repo}";
            var repoId = await store.RecordAsync(tenantId, new MemoryNodeInput(MemoryKinds.Repo, "repo:" + repoKey, repoKey, null, repoKey), ct).ConfigureAwait(false);
            var prId = await store.RecordAsync(tenantId, new MemoryNodeInput(MemoryKinds.PullRequest, "pr:" + prUrl, prTitle, summary, repoKey, prUrl), ct).ConfigureAwait(false);
            await store.LinkAsync(tenantId, prId, repoId, MemoryRels.InRepo, ct).ConfigureAwait(false);
            if (issueNumber is > 0)
            {
                var ticketId = await store.RecordAsync(tenantId, new MemoryNodeInput(MemoryKinds.Ticket, $"ticket:{repoKey}#{issueNumber}",
                    ticketTitle, null, repoKey), ct).ConfigureAwait(false);
                await store.LinkAsync(tenantId, ticketId, repoId, MemoryRels.InRepo, ct).ConfigureAwait(false);
                await store.LinkAsync(tenantId, ticketId, prId, MemoryRels.SolvedBy, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a memory hiccup never fails the task.
        }
    }

    /// <summary>
    /// A lesson: what a review, a failing check or an acceptance test said about a proposed change. Identical text is one
    /// lesson (content-keyed), linked to the pull request it came from.
    /// </summary>
    public static async Task RecordLessonAsync(IMemoryStore store, string tenantId, string repoKey, string? prUrl, string text,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(text)) { return; }
        try
        {
            var source = SourceOf(text);
            var key = "lesson:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repoKey + "\n" + text.Trim())))[..24].ToLowerInvariant();
            var firstLine = text.Trim().Split('\n', 2)[0].Trim();
            var title = $"[{source}] " + (firstLine.Length > 140 ? firstLine[..140] + "…" : firstLine);
            var lessonId = await store.RecordAsync(tenantId, new MemoryNodeInput(MemoryKinds.Lesson, key, title, text.Trim(), repoKey, prUrl), ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(prUrl))
            {
                var prId = await store.RecordAsync(tenantId, new MemoryNodeInput(MemoryKinds.PullRequest, "pr:" + prUrl, prUrl, null, repoKey, prUrl), ct).ConfigureAwait(false);
                await store.LinkAsync(tenantId, lessonId, prId, MemoryRels.LearnedFrom, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort.
        }
    }

    /// <summary>A project practice (content-keyed: the same text is one practice). Returns its node id.</summary>
    public static async Task<Guid> RecordPracticeAsync(IMemoryStore store, string tenantId, string repoKey, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var clean = (text ?? "").Replace("\r", "", StringComparison.Ordinal).Trim();
        if (clean.Length == 0) { return Guid.Empty; }
        var key = "practice:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repoKey + "\n" + clean.ToLowerInvariant())))[..24].ToLowerInvariant();
        var title = clean.Length > 140 ? clean[..140] + "…" : clean;
        return await store.RecordAsync(tenantId, new MemoryNodeInput(MemoryKinds.Practice, key, title, clean, repoKey), ct).ConfigureAwait(false);
    }

    /// <summary>The project's practices as a prompt block, fenced as data. Empty when there are none.</summary>
    public static string PracticesBlock(IReadOnlyList<MemoryNode> practices)
    {
        ArgumentNullException.ThrowIfNull(practices);
        if (practices.Count == 0) { return string.Empty; }
        var text = string.Join("\n", practices.Select(p => "- " + (p.Body ?? p.Title).Replace("\n", " ", StringComparison.Ordinal).Trim()
            + (p.Pinned ? " (pinned by the team)" : "")));
        return "\n\nThis project's practices — distilled from earlier reviews, failing checks and acceptance tests. Follow them:\n"
            + UntrustedText.Wrap("project-practices", text, maxChars: 6_000);
    }

    /// <summary>Where a lesson came from, from the way the request was worded.</summary>
    public static string SourceOf(string text) =>
        text.StartsWith("The pull request's automated checks failed", StringComparison.Ordinal) ? "checks"
        : text.StartsWith("Acceptance testing failed", StringComparison.Ordinal) ? "testing"
        : "review";

    /// <summary>The prompt block of lessons for a task, fenced as data (they quote reviews, logs and code). Empty when
    /// there are none.</summary>
    public static string PromptBlock(IReadOnlyList<MemoryNode> lessons, int maxCharsPerLesson = 600)
    {
        ArgumentNullException.ThrowIfNull(lessons);
        if (lessons.Count == 0) { return string.Empty; }
        var text = string.Join("\n", lessons.Select(l =>
        {
            var body = (l.Body ?? "").Replace("\r", "", StringComparison.Ordinal).Trim();
            if (body.Length > maxCharsPerLesson) { body = body[..maxCharsPerLesson] + "…"; }
            return $"- {l.Title}{(l.Pinned ? " (pinned by the team)" : "")}\n  {body.Replace("\n", "\n  ", StringComparison.Ordinal)}";
        }));
        return "\n\nWhat earlier work on this project taught us — reviews, failing checks and acceptance tests. Apply what is "
            + "relevant so the same problems don't come back:\n" + UntrustedText.Wrap("project-memory", text, maxChars: 8_000);
    }
}
