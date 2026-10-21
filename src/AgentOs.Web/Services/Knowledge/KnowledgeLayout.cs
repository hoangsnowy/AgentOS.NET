// Board › Knowledge: a project's knowledge graph laid out for an SVG — the repo in the centre, its tickets on the
// first ring, each ticket's proposed change just outside it, the lessons a change taught on the outer ring next to
// it. Deterministic (no physics, no JS), O(nodes + edges), so it renders in one pass and reads the same every time.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentOs.Domain.Memory;

namespace AgentOs.Web.Services.Knowledge;

/// <summary>A positioned node (SVG user units, centre 0,0).</summary>
public sealed record PlacedNode(MemoryNode Node, double X, double Y);

/// <summary>A drawn edge.</summary>
public sealed record PlacedEdge(double X1, double Y1, double X2, double Y2, string Rel);

/// <summary>The laid-out graph.</summary>
public sealed record KnowledgePicture(IReadOnlyList<PlacedNode> Nodes, IReadOnlyList<PlacedEdge> Edges);

public static class KnowledgeLayout
{
    public const double TicketRing = 130, ChangeRing = 205, LessonRing = 275;

    public static KnowledgePicture Compute(MemoryGraph graph, string repoKey)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var nodes = graph.Nodes.Where(n => n.RepoKey == repoKey).ToDictionary(n => n.Id);
        var placed = new Dictionary<Guid, PlacedNode>();
        var repo = nodes.Values.FirstOrDefault(n => n.Kind == MemoryKinds.Repo);
        if (repo is not null) { placed[repo.Id] = new PlacedNode(repo, 0, 0); }

        // Who points at whom: ticket -solved_by-> PR, lesson -learned_from-> PR.
        var prOfTicket = graph.Edges.Where(e => e.Rel == MemoryRels.SolvedBy).ToLookup(e => e.From, e => e.To);
        var lessonsOfPr = graph.Edges.Where(e => e.Rel == MemoryRels.LearnedFrom).ToLookup(e => e.To, e => e.From);

        var tickets = nodes.Values.Where(n => n.Kind == MemoryKinds.Ticket).OrderBy(n => n.Key, StringComparer.Ordinal).ToList();
        var ticketedPrs = graph.Edges.Where(e => e.Rel == MemoryRels.SolvedBy).Select(e => e.To).ToHashSet();
        var orphanPrs = nodes.Values.Where(n => n.Kind == MemoryKinds.PullRequest && !ticketedPrs.Contains(n.Id)).ToList();
        var slots = Math.Max(1, tickets.Count + orphanPrs.Count);
        var step = 2 * Math.PI / slots;
        var slot = 0;

        foreach (var t in tickets)
        {
            var angle = slot++ * step - Math.PI / 2;
            placed[t.Id] = At(t, TicketRing, angle);
            PlaceChanges(prOfTicket[t.Id], angle);
        }
        foreach (var pr in orphanPrs)
        {
            PlaceChanges([pr.Id], slot++ * step - Math.PI / 2);
        }
        // Lessons not tied to a placed change: spread on the outer ring where there is room.
        var loose = nodes.Values.Where(n => n.Kind == MemoryKinds.Lesson && !placed.ContainsKey(n.Id)).ToList();
        for (var i = 0; i < loose.Count; i++)
        {
            placed[loose[i].Id] = At(loose[i], LessonRing + 30, (i + 0.5) * 2 * Math.PI / loose.Count - Math.PI / 2);
        }

        var edges = graph.Edges
            .Where(e => placed.ContainsKey(e.From) && placed.ContainsKey(e.To))
            .Select(e => new PlacedEdge(placed[e.From].X, placed[e.From].Y, placed[e.To].X, placed[e.To].Y, e.Rel))
            .ToList();
        return new KnowledgePicture(placed.Values.ToList(), edges);

        void PlaceChanges(IEnumerable<Guid> prIds, double angle)
        {
            var prs = prIds.Where(nodes.ContainsKey).ToList();
            for (var i = 0; i < prs.Count; i++)
            {
                var a = angle + (i - (prs.Count - 1) / 2.0) * step / Math.Max(2, prs.Count + 1);
                placed[prs[i]] = At(nodes[prs[i]], ChangeRing, a);
                var lessons = lessonsOfPr[prs[i]].Where(nodes.ContainsKey).ToList();
                for (var j = 0; j < lessons.Count; j++)
                {
                    var la = a + (j - (lessons.Count - 1) / 2.0) * step / Math.Max(3, lessons.Count + 2);
                    placed[lessons[j]] = At(nodes[lessons[j]], LessonRing, la);
                }
            }
        }
    }

    private static PlacedNode At(MemoryNode n, double r, double angle) =>
        new(n, Math.Round(r * Math.Cos(angle), 1), Math.Round(r * Math.Sin(angle), 1));
}
