// Provider-neutral graph DTOs + validation/run result types for the OrchestrationStudio executor.
// The Web layer owns the rich GraphNode/GraphEdge (canvas) types; it maps them into these minimal mirrors
// so the executable core stays in the Pipeline module — no Blazor / diagram / Web dependency, fully testable.

using System.Collections.Generic;

namespace AgentOs.Domain.Pipeline.Graph;

/// <summary>Minimal mirror of a canvas node — only what the executor needs. The free-form fields
/// (<paramref name="Description"/>/<paramref name="Input"/>/<paramref name="Output"/>) drive the
/// non-agent nodes: an Llm node reads its prompt from <paramref name="Description"/>, a Tool node reads
/// its <c>{"tool","input"}</c> JSON from <paramref name="Description"/>, and every node writes its result
/// into the shared state under <paramref name="Output"/> (and reads inputs from <paramref name="Input"/>).</summary>
public sealed record PlanNode(
    string Id,
    string StepType,
    string? AgentRole,
    string Title,
    int MaxIterations,
    bool IsStart,
    string Description = "",
    string Input = "",
    string Output = "",
    IReadOnlyList<string>? Routes = null);

/// <summary>Minimal mirror of a canvas edge.</summary>
public sealed record PlanEdge(string Id, string SourceId, string TargetId, string Label);

/// <summary>A graph to validate + run.</summary>
public sealed record PlanGraph(string Id, string Name, IReadOnlyList<PlanNode> Nodes, IReadOnlyList<PlanEdge> Edges);

/// <summary>Per-node support verdict.</summary>
public enum NodeSupport
{
    /// <summary>An agent node with a known role, the QA gate (Evaluator), or End.</summary>
    Supported,

    /// <summary>A node type the executor can't run yet (Tool/Parallel/Human/…).</summary>
    UnsupportedType,

    /// <summary>An Agent node whose AgentRole isn't one of Requirement/Coding/Testing/Qa.</summary>
    UnknownAgentRole,
}

/// <summary>Why a node is (un)supported.</summary>
public sealed record NodeValidation(string NodeId, NodeSupport Support, string Reason);

/// <summary>The result of validating a graph: runnable-or-not + per-node verdicts + best-effort order.</summary>
public sealed record GraphValidationResult(
    bool IsRunnable,
    string? StartNodeId,
    IReadOnlyList<NodeValidation> Nodes,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> LinearOrder);

/// <summary>Live per-node status pushed to the canvas during a run.</summary>
public enum GraphNodePhase { Pending, Running, Done, Failed, Skipped }

/// <summary>One node-status update (node-id correlated, unlike the 5-value PipelineStage).
/// <paramref name="Offline"/> = the node finished on canned offline output — no model was called.</summary>
public sealed record GraphNodeEvent(string NodeId, GraphNodePhase Phase, string? Meta, string? Message, bool Offline = false);

/// <summary>A Human node is asking the operator to approve/answer before the run continues. Surfaced to the
/// caller via the run's human callback; the run pauses at the node until a <see cref="GraphHumanReply"/> is
/// returned.</summary>
public sealed record GraphHumanRequest(string NodeId, string Title, string Question, string Context);

/// <summary>The operator's answer to a <see cref="GraphHumanRequest"/>. <paramref name="Approved"/> = false
/// stops the run at that node (rejection); <paramref name="Note"/> is an optional free-text answer/reason.</summary>
public sealed record GraphHumanReply(bool Approved, string? Note = null);

/// <summary>The outcome of a graph run. <paramref name="Offline"/> = at least one node was answered by the
/// canned offline client, so the run is a dry run and must not be presented or counted as live output.</summary>
public sealed record GraphRunResult(bool Completed, string? FailureMessage, bool Offline = false)
{
    /// <summary>Tokens and cost per agent for this run, in the order the agents first ran. Empty when the
    /// run stopped before any model call.</summary>
    public IReadOnlyList<AgentTokenUsage> Usage { get; init; } = [];
}

/// <summary>One agent's share of a run: how many calls it made and the tokens/cost they used.
/// <paramref name="Estimated"/> = some of the calls were answered offline, so their tokens are an estimate
/// (text length ÷ 4), not provider-reported usage.</summary>
public sealed record AgentTokenUsage(string Agent, int Calls, int TokensIn, int TokensOut, decimal CostUsd, bool Estimated)
{
    /// <summary>Input + output tokens.</summary>
    public int TotalTokens => TokensIn + TokensOut;
}
