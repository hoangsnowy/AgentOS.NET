// The ticket flow as data (ADR-0016): the per-ticket graph Autopilot runs — Code → Checks → Review → Test → Merge by
// default — is a definition the owner sees and edits in the Workflow app (Workflow › Ticket flow). Both engines run it:
// the MAF workflow (TicketWorkflow builds its graph from it) and the step-by-step rules (AutopilotPlanner skips the
// steps it leaves out for a ticket). A definition is ordered step nodes joined by conditional edges: "pass" leads on
// to the next step, "fail" sends the change back to Code (bounded by the task's persisted fix rounds against
// Settings › Autopilot › Automatic fix rounds — there is no second counter).
//
// Safety invariants (TicketFlowValidator — checked on save AND when a run loads it; a definition that breaks one is
// refused with a plain-language reason and the run falls back to Default): it starts at Code, ends at Merge, Checks
// always runs before Merge, every ticket passes at least one merge gate (Review / Test) before Merge whatever its
// labels, no cycle except a fail edge back to Code, every step reachable, steps in their natural order.
//
// Adding a step kind (e.g. the upcoming Design/screenshot gate) is local: one TicketStepKind value, one
// TicketStepKinds entry (rank, agent roles, whether it is a merge gate / can send a change back), one executor in
// TicketWorkflow and — if the step-by-step rules should run it — one move in AutopilotPlanner.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>What a step of a ticket's flow does. Values are stored by name — never rename one.</summary>
public enum TicketStepKind
{
    /// <summary>The Coding (tickets) agent implements the ticket (and every fix sent back to it).</summary>
    Code,

    /// <summary>The pull request's automated checks (CI) and whether it merges cleanly.</summary>
    Checks,

    /// <summary>The Reviewer agent reviews the change.</summary>
    Review,

    /// <summary>The Testing (tickets) agent tests the change against the ticket's acceptance criteria.</summary>
    Test,

    /// <summary>The change is merged (or left for a person when the board doesn't auto-merge).</summary>
    Merge,
}

/// <summary>A step kind's fixed traits — the one place a new kind is declared.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Rank">Its natural position: steps run in rank order (Code first, Merge last).</param>
/// <param name="Label">The name people see.</param>
/// <param name="Description">One sentence: what the step does.</param>
/// <param name="Roles">The agent roles (<see cref="AgentRoles"/> keys) that can run it, the default first; empty = no agent.</param>
/// <param name="Required">Always runs, for every ticket: can't be turned off or limited by labels.</param>
/// <param name="CanSendBack">A failure sends the change back to Code (a fix round).</param>
/// <param name="IsMergeGate">Counts as a quality gate: every ticket passes at least one before Merge.</param>
public sealed record TicketStepKindInfo(TicketStepKind Kind, int Rank, string Label, string Description, IReadOnlyList<string> Roles,
    bool Required, bool CanSendBack, bool IsMergeGate)
{
    /// <summary>The agent role that runs the step when its node names none.</summary>
    public string? DefaultRole => Roles.Count > 0 ? Roles[0] : null;
}

/// <summary>The registry of step kinds.</summary>
public static class TicketStepKinds
{
    /// <summary>Every kind, in rank order.</summary>
    public static IReadOnlyList<TicketStepKindInfo> All { get; } =
    [
        new(TicketStepKind.Code, 0, "Code", "The Coding (tickets) agent implements the ticket and pushes a pull request; every fix comes back here.",
            [AgentRoles.IssueWork], Required: true, CanSendBack: false, IsMergeGate: false),
        new(TicketStepKind.Checks, 10, "Checks", "Waits for the pull request's automated checks; red checks or a conflict go back to Code.",
            [], Required: true, CanSendBack: true, IsMergeGate: false),
        new(TicketStepKind.Review, 20, "Review", "The Reviewer agent reviews the change; must-fix findings go back to Code.",
            [AgentRoles.Reviewer], Required: false, CanSendBack: true, IsMergeGate: true),
        new(TicketStepKind.Test, 30, "Test", "The Testing (tickets) agent tests the change against the ticket's acceptance criteria; failures go back to Code.",
            [AgentRoles.Acceptance], Required: false, CanSendBack: true, IsMergeGate: true),
        new(TicketStepKind.Merge, 100, "Merge", "Merges the change — or leaves it ready for a person when the project doesn't auto-merge.",
            [], Required: true, CanSendBack: false, IsMergeGate: false),
    ];

    private static readonly FrozenDictionary<TicketStepKind, TicketStepKindInfo> ByKind = All.ToFrozenDictionary(k => k.Kind);

    /// <summary>The traits of <paramref name="kind"/>.</summary>
    public static TicketStepKindInfo Of(TicketStepKind kind) =>
        ByKind.TryGetValue(kind, out var info) ? info : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown ticket step kind.");

    /// <summary>The merge gates' names, joined for a sentence ("neither Review nor Test").</summary>
    public static string NeitherGate()
    {
        var gates = All.Where(k => k.IsMergeGate).Select(k => k.Label).ToList();
        return gates.Count switch
        {
            0 => "no gate",
            1 => "no " + gates[0],
            2 => $"neither {gates[0]} nor {gates[1]}",
            _ => "none of " + string.Join(", ", gates),
        };
    }

    /// <summary>The merge gates' names as alternatives ("Review or Test").</summary>
    public static string EitherGate() => string.Join(" or ", All.Where(k => k.IsMergeGate).Select(k => k.Label));
}

/// <summary>One step of a ticket flow.</summary>
/// <param name="Id">Stable id within the definition (edges name it).</param>
/// <param name="Kind">What the step does.</param>
/// <param name="Enabled">Off = every ticket passes through it.</param>
/// <param name="Role">The agent role (<see cref="AgentRoles"/> key) that runs it; null = the kind's default.</param>
/// <param name="SkipLabels">Tickets with any of these labels pass through the step.</param>
/// <param name="OnlyLabels">When set, only tickets with one of these labels run the step.</param>
public sealed record TicketFlowNode(string Id, TicketStepKind Kind, bool Enabled = true, string? Role = null,
    IReadOnlyList<string>? SkipLabels = null, IReadOnlyList<string>? OnlyLabels = null)
{
    /// <summary>The kind's traits.</summary>
    [JsonIgnore] public TicketStepKindInfo Info => TicketStepKinds.Of(Kind);

    /// <summary>The agent role that runs the step (its own or the kind's default), or null for a step with no agent.</summary>
    [JsonIgnore] public string? EffectiveRole => Role ?? Info.DefaultRole;

    /// <summary>Skip labels, never null.</summary>
    [JsonIgnore] public IReadOnlyList<string> Skip => SkipLabels ?? [];

    /// <summary>Only labels, never null.</summary>
    [JsonIgnore] public IReadOnlyList<string> Only => OnlyLabels ?? [];

    /// <summary>True when a ticket with <paramref name="labels"/> runs this step.</summary>
    public bool AppliesTo(IReadOnlyCollection<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (!Enabled) { return false; }
        if (Skip.Any(s => labels.Contains(s, StringComparer.OrdinalIgnoreCase))) { return false; }
        return Only.Count == 0 || Only.Any(o => labels.Contains(o, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Who runs it and for which tickets, in a few words ("Reviewer · not type:chore").</summary>
    public string Conditions()
    {
        var parts = new List<string>();
        if (Only.Count > 0) { parts.Add("only " + string.Join(", ", Only)); }
        if (Skip.Count > 0) { parts.Add("not " + string.Join(", ", Skip)); }
        return string.Join(" · ", parts);
    }
}

/// <summary>When an edge is followed.</summary>
public enum TicketFlowEdgeWhen
{
    /// <summary>The step passed: on to the next one.</summary>
    Pass,

    /// <summary>The step failed: the change goes back to Code (a fix round).</summary>
    Fail,
}

/// <summary>A conditional edge between two steps.</summary>
public sealed record TicketFlowEdge(string From, string To, TicketFlowEdgeWhen When);

/// <summary>A ticket flow: ordered step nodes joined by conditional edges.</summary>
public sealed record TicketFlowDefinition(IReadOnlyList<TicketFlowNode> Nodes, IReadOnlyList<TicketFlowEdge> Edges)
{
    /// <summary>The built-in flow — what Autopilot has always run: Code → Checks → Review → Test → Merge, every failure
    /// back to Code.</summary>
    public static TicketFlowDefinition Default { get; } = Chain(
    [
        new("code", TicketStepKind.Code),
        new("checks", TicketStepKind.Checks),
        new("review", TicketStepKind.Review),
        new("test", TicketStepKind.Test),
        new("merge", TicketStepKind.Merge),
    ]);

    /// <summary>Steps in the order given, each passing to the next, every step that can fail sending the change back
    /// to the Code step.</summary>
    public static TicketFlowDefinition Chain(IReadOnlyList<TicketFlowNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var edges = new List<TicketFlowEdge>();
        for (var i = 0; i + 1 < nodes.Count; i++) { edges.Add(new(nodes[i].Id, nodes[i + 1].Id, TicketFlowEdgeWhen.Pass)); }
        var code = nodes.FirstOrDefault(n => n.Kind == TicketStepKind.Code);
        if (code is not null)
        {
            edges.AddRange(nodes.Where(n => n.Info.CanSendBack).Select(n => new TicketFlowEdge(n.Id, code.Id, TicketFlowEdgeWhen.Fail)));
        }
        return new(nodes, edges);
    }

    /// <summary>This definition with <paramref name="node"/> in place of the node with its id.</summary>
    public TicketFlowDefinition With(TicketFlowNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return this with { Nodes = Nodes.Select(n => n.Id == node.Id ? node : n).ToList() };
    }

    /// <summary>The node with <paramref name="id"/>, or null.</summary>
    public TicketFlowNode? Find(string id) => Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));

    /// <summary>The steps in run order: from Code along the pass edges (a malformed definition stops where it breaks).</summary>
    public IReadOnlyList<TicketFlowNode> Ordered()
    {
        var byId = Nodes.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var next = Edges.Where(e => e.When == TicketFlowEdgeWhen.Pass).GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().To, StringComparer.Ordinal);
        var order = new List<TicketFlowNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur = Nodes.FirstOrDefault(n => n.Kind == TicketStepKind.Code);
        while (cur is not null && seen.Add(cur.Id))
        {
            order.Add(cur);
            cur = next.TryGetValue(cur.Id, out var to) && byId.TryGetValue(to, out var n) ? n : null;
        }
        return order;
    }

    /// <summary>What a ticket with <paramref name="labels"/> runs: the steps that apply to it, in order.</summary>
    public TicketFlowPlan Resolve(IReadOnlyCollection<string>? labels)
    {
        var l = labels ?? [];
        var ordered = Ordered();
        return new TicketFlowPlan(ordered.Where(n => n.AppliesTo(l)).ToList(), ordered.Where(n => !n.AppliesTo(l)).ToList());
    }

    /// <summary>The flow in one line ("Code → Checks → Review → Test → Merge"); a step limited by labels says so.</summary>
    public string Summary() =>
        string.Join(" → ", Ordered().Where(n => n.Enabled).Select(n => n.Conditions() is { Length: > 0 } c ? $"{n.Info.Label} ({c})" : n.Info.Label));

    /// <summary>Serialized for storage.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, TicketFlowJsonContext.Default.TicketFlowDefinition);

    /// <summary>A stored definition, or null when it can't be read (unknown step kind, broken JSON).</summary>
    public static TicketFlowDefinition? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try
        {
            var def = JsonSerializer.Deserialize(json, TicketFlowJsonContext.Default.TicketFlowDefinition);
            return def is { Nodes: not null, Edges: not null } ? def : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>The steps one ticket runs, in order (Code first, Merge last), and those it passes through.</summary>
public sealed class TicketFlowPlan(IReadOnlyList<TicketFlowNode> steps, IReadOnlyList<TicketFlowNode> skipped)
{
    /// <summary>The steps the ticket runs, in order.</summary>
    public IReadOnlyList<TicketFlowNode> Steps { get; } = steps;

    /// <summary>The steps it passes through (off, or not for its labels).</summary>
    public IReadOnlyList<TicketFlowNode> Skipped { get; } = skipped;

    /// <summary>The default flow's plan — every step.</summary>
    public static TicketFlowPlan Default { get; } = TicketFlowDefinition.Default.Resolve([]);

    /// <summary>True when the ticket runs a step of <paramref name="kind"/>.</summary>
    public bool Runs(TicketStepKind kind) => Steps.Any(s => s.Kind == kind);

    /// <summary>The steps in one line ("Code → Checks → Test → Merge").</summary>
    public string Summary => string.Join(" → ", Steps.Select(s => s.Info.Label));

    /// <summary>Why this plan must not run (the run-time twin of the save-time invariants), or null when it is safe.</summary>
    public string? SafetyProblem()
    {
        if (Steps.Count == 0 || Steps[0].Kind != TicketStepKind.Code) { return "it doesn't start with Code"; }
        if (Steps[^1].Kind != TicketStepKind.Merge) { return "it doesn't end with Merge"; }
        if (!Runs(TicketStepKind.Checks)) { return "Checks wouldn't run before Merge"; }
        if (!Steps.Any(s => s.Info.IsMergeGate)) { return $"the ticket would reach Merge with {TicketStepKinds.NeitherGate()}"; }
        for (var i = 1; i < Steps.Count; i++)
        {
            if (Steps[i].Info.Rank <= Steps[i - 1].Info.Rank) { return $"{Steps[i].Info.Label} comes after {Steps[i - 1].Info.Label}"; }
        }
        return null;
    }
}

/// <summary>A reason a ticket flow can't be used — attached to a step when it is about one.</summary>
public sealed record TicketFlowProblem(string? NodeId, string Message);

/// <summary>The safety invariants of a ticket flow.</summary>
public static class TicketFlowValidator
{
    /// <summary>Most steps a flow may have.</summary>
    public const int MaxNodes = 16;

    /// <summary>Most labels a step's filter may list.</summary>
    public const int MaxLabelsPerFilter = 8;

    /// <summary>Most distinct labels across the gates' filters (each combination is checked).</summary>
    public const int MaxDistinctLabels = 12;

    /// <summary>Longest label.</summary>
    public const int MaxLabelLength = 50;

    /// <summary>Every reason <paramref name="definition"/> can't be used, in plain language; empty when it is safe.</summary>
    public static IReadOnlyList<TicketFlowProblem> Validate(TicketFlowDefinition? definition)
    {
        var problems = new List<TicketFlowProblem>();
        if (definition is null || definition.Nodes is null || definition.Edges is null)
        {
            problems.Add(new(null, "The ticket flow couldn't be read."));
            return problems;
        }
        var nodes = definition.Nodes;
        if (nodes.Count == 0) { problems.Add(new(null, "The ticket flow has no steps.")); return problems; }
        if (nodes.Count > MaxNodes) { problems.Add(new(null, $"A ticket flow has at most {MaxNodes} steps.")); return problems; }

        // Ids and kinds.
        var byId = new Dictionary<string, TicketFlowNode>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id) || n.Id.Length > 40) { problems.Add(new(null, "Every step needs a short id.")); continue; }
            if (!Enum.IsDefined(n.Kind)) { problems.Add(new(n.Id, $"Step {n.Id} is of an unknown kind.")); continue; }
            if (!byId.TryAdd(n.Id, n)) { problems.Add(new(n.Id, $"Two steps share the id {n.Id}.")); }
        }
        if (problems.Count > 0) { return problems; }
        foreach (var dup in nodes.GroupBy(n => n.Kind).Where(g => g.Count() > 1))
        {
            problems.Add(new(dup.Skip(1).First().Id, $"There can be only one {TicketStepKinds.Of(dup.Key).Label} step."));
        }
        var code = nodes.FirstOrDefault(n => n.Kind == TicketStepKind.Code);
        var merge = nodes.FirstOrDefault(n => n.Kind == TicketStepKind.Merge);
        if (code is null) { problems.Add(new(null, "The flow must start with Code — every ticket starts by coding it.")); }
        if (merge is null) { problems.Add(new(null, "The flow must end with Merge.")); }
        if (nodes.All(n => n.Kind != TicketStepKind.Checks)) { problems.Add(new(null, "Checks must run before Merge — add it back.")); }

        // Each step on its own: required steps always run, roles fit the step, label filters are sane.
        foreach (var n in nodes)
        {
            var info = n.Info;
            if (info.Required && !n.Enabled) { problems.Add(new(n.Id, $"{info.Label} always runs — it can't be turned off.")); }
            if (info.Required && (n.Skip.Count > 0 || n.Only.Count > 0))
            {
                problems.Add(new(n.Id, $"{info.Label} runs for every ticket — it can't be limited by labels."));
            }
            if (n.Role is { } role)
            {
                if (info.Roles.Count == 0) { problems.Add(new(n.Id, $"{info.Label} isn't run by an agent, so it can't name one.")); }
                else if (!info.Roles.Contains(role, StringComparer.Ordinal))
                {
                    var allowed = string.Join(" or ", info.Roles.Select(AgentRoles.Label));
                    problems.Add(new(n.Id, AgentRoles.Find(role) is null
                        ? $"{info.Label} names an unknown agent ({role}) — pick {allowed}."
                        : $"{info.Label} can't be run by {AgentRoles.Label(role)} — pick {allowed}."));
                }
            }
            foreach (var (list, what) in new[] { (n.Skip, "skip"), (n.Only, "only") })
            {
                if (list.Count > MaxLabelsPerFilter) { problems.Add(new(n.Id, $"{info.Label}: list at most {MaxLabelsPerFilter} labels to {what}.")); }
                if (list.Any(l => string.IsNullOrWhiteSpace(l) || l.Length > MaxLabelLength || l.Trim().Length != l.Length || l.Contains(',', StringComparison.Ordinal)))
                {
                    problems.Add(new(n.Id, $"{info.Label}: a label is empty, too long or has stray spaces or commas."));
                }
            }
            foreach (var both in n.Skip.Intersect(n.Only, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(new(n.Id, $"{info.Label} both requires and skips {both}."));
            }
        }

        // Edges: known steps; pass = one next step each (none after Merge, none into Code); fail = back to Code only.
        foreach (var e in definition.Edges)
        {
            if (!byId.ContainsKey(e.From) || !byId.ContainsKey(e.To))
            {
                problems.Add(new(null, $"An edge points at a step that doesn't exist ({e.From} → {e.To})."));
            }
        }
        var edges = definition.Edges.Where(e => byId.ContainsKey(e.From) && byId.ContainsKey(e.To)).ToList();
        var pass = edges.Where(e => e.When == TicketFlowEdgeWhen.Pass).ToList();
        var fail = edges.Where(e => e.When == TicketFlowEdgeWhen.Fail).ToList();
        foreach (var n in nodes)
        {
            var info = n.Info;
            var outs = pass.Count(e => e.From == n.Id);
            if (n.Kind == TicketStepKind.Merge && outs > 0) { problems.Add(new(n.Id, "Nothing may follow Merge — it is the last step.")); }
            else if (n.Kind != TicketStepKind.Merge && outs == 0) { problems.Add(new(n.Id, $"{info.Label} has no next step — every step leads on to Merge.")); }
            else if (outs > 1) { problems.Add(new(n.Id, $"{info.Label} has more than one next step.")); }
            var backs = fail.Where(e => e.From == n.Id).ToList();
            if (!info.CanSendBack && backs.Count > 0) { problems.Add(new(n.Id, $"{info.Label} can't send a change back.")); }
            if (info.CanSendBack && backs.Count == 0) { problems.Add(new(n.Id, $"{info.Label} needs its way back to Code for when it fails.")); }
            if (backs.Count > 1) { problems.Add(new(n.Id, $"{info.Label} has more than one way back.")); }
            if (backs.Any(e => byId[e.To].Kind != TicketStepKind.Code)) { problems.Add(new(n.Id, $"A failed {info.Label} can only go back to Code.")); }
        }
        if (code is not null && pass.Any(e => e.To == code.Id))
        {
            problems.Add(new(code.Id, "Nothing may come before Code — every ticket starts by coding it."));
        }
        if (problems.Count > 0 || code is null) { return problems; }

        // The pass chain from Code: no cycle, ends at Merge, reaches every step, in rank order.
        var chain = new List<TicketFlowNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var cur = code; cur is not null;)
        {
            if (!seen.Add(cur.Id)) { problems.Add(new(cur.Id, "The steps loop back on themselves — only a failure may go back to Code.")); return problems; }
            chain.Add(cur);
            var to = pass.FirstOrDefault(e => e.From == cur.Id)?.To;
            cur = to is null ? null : byId[to];
        }
        if (chain[^1].Kind != TicketStepKind.Merge) { problems.Add(new(chain[^1].Id, "The flow must end with Merge.")); }
        foreach (var n in nodes.Where(n => !seen.Contains(n.Id)))
        {
            problems.Add(new(n.Id, $"{n.Info.Label} can't be reached from Code."));
        }
        for (var i = 1; i < chain.Count; i++)
        {
            if (chain[i].Info.Rank <= chain[i - 1].Info.Rank)
            {
                problems.Add(new(chain[i].Id, $"{chain[i].Info.Label} must come before {chain[i - 1].Info.Label}."));
            }
        }
        if (problems.Count > 0) { return problems; }

        // Every ticket — whatever its labels — passes at least one merge gate before Merge.
        if (GateProblem(chain) is { } gate) { problems.Add(gate); }
        return problems;
    }

    /// <summary>True when <paramref name="definition"/> keeps every invariant.</summary>
    public static bool IsValid(TicketFlowDefinition? definition) => Validate(definition).Count == 0;

    // Only the labels named in the gates' filters change which gates run, so checking every combination of those
    // (smallest first, so the reason names the simplest ticket) covers every possible ticket.
    private static TicketFlowProblem? GateProblem(IReadOnlyList<TicketFlowNode> chain)
    {
        var gates = chain.Where(n => n.Info.IsMergeGate && n.Enabled).ToList();
        if (gates.Count == 0)
        {
            return new(null, $"Turn on {TicketStepKinds.EitherGate()}: every ticket needs at least one of them before Merge.");
        }
        var labels = gates.SelectMany(g => g.Skip.Concat(g.Only)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (labels.Count > MaxDistinctLabels)
        {
            return new(null, $"Use at most {MaxDistinctLabels} different labels in the steps' filters.");
        }
        var subsets = Enumerable.Range(0, 1 << labels.Count)
            .OrderBy(m => System.Numerics.BitOperations.PopCount((uint)m)).ThenBy(m => m);
        foreach (var mask in subsets)
        {
            var ticket = labels.Where((_, i) => (mask & (1 << i)) != 0).ToList();
            if (gates.Any(g => g.AppliesTo(ticket))) { continue; }
            var neither = TicketStepKinds.NeitherGate();
            return ticket.Count == 0
                ? new(null, $"A ticket with none of the labels {string.Join(", ", labels)} would reach Merge with {neither}.")
                : new(null, string.Create(CultureInfo.InvariantCulture,
                    $"A ticket labelled {string.Join(" and ", ticket)} would reach Merge with {neither}."));
        }
        return null;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TicketFlowDefinition))]
internal sealed partial class TicketFlowJsonContext : JsonSerializerContext;
