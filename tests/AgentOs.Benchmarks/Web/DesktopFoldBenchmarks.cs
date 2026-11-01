// Per-render / per-keystroke folds in the Web desktop: Insights groups its cost and latency buckets by agent role on
// every render, Activities search scans every settings page + descriptor on every keystroke, and Board › Knowledge
// lays out the project graph on every render. Blazor Server runs these on the circuit, so their cost is server CPU
// per connected desktop.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AgentOs.Benchmarks.Settings;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Memory;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Web.Components.Pages;
using AgentOs.Web.Services;
using AgentOs.Web.Services.Knowledge;
using BenchmarkDotNet.Attributes;

namespace AgentOs.Benchmarks.Web;

/// <summary>Insights' by-role folds and the role canonicalisation behind them.</summary>
[MemoryDiagnoser]
public class InsightsFoldBenchmarks
{
    // Stored agent names as they appear in a real ledger: today's keys, legacy type names, aliases, odd casing.
    private readonly string[] _storedNames =
    [
        "Coding", "CodingAgent", "Requirement", "RequirementAgent", "Testing", "TestingAgent", "Qa", "QaAgent",
        "Orchestrator", "PipelineOrchestrator", "IssueWork", "IssueWorkAgent", "Reviewer", "ReviewAgent", "Acceptance",
        "AcceptanceAgent", "Discovery", "TicketDecomposer", "TicketDecomposerAgent", "Decomposer", "Retrospective",
        "RetrospectiveAgent", "AutopilotOrchestrator", "AutopilotOrchestratorAgent", "custom-plugin-agent", "coding",
    ];

    private CostBucket[] _costBuckets = null!;
    private AgentLatencyBucket[] _latencyBuckets = null!;

    /// <summary>One bucket per stored name (what the ledger's GROUP BY agent returns).</summary>
    [GlobalSetup]
    public void Setup()
    {
        _costBuckets = _storedNames.Select((n, i) => new CostBucket(n, 0.37m * (i + 1), 12_000 * (i + 1), 1_800 * (i + 1), 3 + i, 4_000L * i, 600L * i)).ToArray();
        _latencyBuckets = _storedNames.Select((n, i) => new AgentLatencyBucket(n, 850.0 + 97.0 * i, 3 + i)).ToArray();
    }

    /// <summary>Canonicalise every stored name once (the per-bucket cost inside both folds).</summary>
    [Benchmark]
    public int CanonicalAllNames()
    {
        var total = 0;
        foreach (var n in _storedNames)
        {
            total += AgentRoles.Canonical(n).Length;
        }
        return total;
    }

    /// <summary>The "By agent" chart fold, materialised the way the page does.</summary>
    [Benchmark(Baseline = true)]
    public List<CostBucket> CostByRole() => InsightsApp.ByRole(_costBuckets).OrderByDescending(a => a.TokensIn + a.TokensOut).ToList();

    /// <summary>The "Latency" chart fold (weighted average per role).</summary>
    [Benchmark]
    public List<AgentLatencyBucket> LatencyByRole() => InsightsApp.LatencyByRole(_latencyBuckets).ToList();
}

/// <summary>Activities / Settings search — runs on every keystroke.</summary>
[MemoryDiagnoser]
public class SettingsSearchBenchmarks
{
    private IReadOnlyList<SettingDescriptor> _descriptors = null!;

    /// <summary>What the person typed.</summary>
    [Params("dark", "coding model", "zzz")]
    public string Query { get; set; } = "";

    /// <summary>The settings catalog (~150 descriptors).</summary>
    [GlobalSetup]
    public void Setup() => _descriptors = SettingsFixture.Catalog().All;

    /// <summary>An admin searching (sees every page).</summary>
    [Benchmark]
    public IReadOnlyList<SettingsHit> Find() => SettingsSearch.Find(Query, _descriptors, isAdmin: true, isPlatformAdmin: true);
}

/// <summary>Board › Knowledge layout — runs on every render of the graph.</summary>
[MemoryDiagnoser]
public class KnowledgeLayoutBenchmarks
{
    private MemoryGraph _graph = null!;

    /// <summary>Tickets on the board (each with one PR and two lessons, plus some orphan PRs and loose lessons).</summary>
    [Params(40, 200)]
    public int Tickets { get; set; }

    /// <summary>Build a graph of the requested size, plus a second repo's nodes that must be filtered out.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var nodes = new List<MemoryNode>();
        var edges = new List<MemoryEdge>();
        var at = DateTimeOffset.UnixEpoch;
        MemoryNode Node(string kind, string key, string repo) =>
            new(Guid.NewGuid(), kind, key, key, null, repo, false, at, null);

        foreach (var repo in new[] { "acme/xiangqi", "acme/other" })
        {
            var root = Node(MemoryKinds.Repo, "repo:" + repo, repo);
            nodes.Add(root);
            for (var t = 0; t < Tickets; t++)
            {
                var ticket = Node(MemoryKinds.Ticket, string.Create(CultureInfo.InvariantCulture, $"ticket:{repo}#{t:D4}"), repo);
                var pr = Node(MemoryKinds.PullRequest, string.Create(CultureInfo.InvariantCulture, $"pr:{repo}#{1000 + t}"), repo);
                nodes.Add(ticket);
                nodes.Add(pr);
                edges.Add(new MemoryEdge(ticket.Id, root.Id, MemoryRels.InRepo));
                edges.Add(new MemoryEdge(ticket.Id, pr.Id, MemoryRels.SolvedBy));
                for (var l = 0; l < 2; l++)
                {
                    var lesson = Node(MemoryKinds.Lesson, string.Create(CultureInfo.InvariantCulture, $"lesson:{repo}#{t}-{l}"), repo);
                    nodes.Add(lesson);
                    edges.Add(new MemoryEdge(lesson.Id, pr.Id, MemoryRels.LearnedFrom));
                }
                if (t % 10 == 0)
                {
                    nodes.Add(Node(MemoryKinds.PullRequest, string.Create(CultureInfo.InvariantCulture, $"pr:{repo}#orphan{t}"), repo));
                    nodes.Add(Node(MemoryKinds.Lesson, string.Create(CultureInfo.InvariantCulture, $"lesson:{repo}#loose{t}"), repo));
                }
            }
        }
        _graph = new MemoryGraph(nodes, edges);
    }

    /// <summary>Lay out one repo's graph.</summary>
    [Benchmark]
    public KnowledgePicture Compute() => KnowledgeLayout.Compute(_graph, "acme/xiangqi");
}
