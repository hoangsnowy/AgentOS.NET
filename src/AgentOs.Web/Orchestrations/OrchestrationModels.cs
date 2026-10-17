// AgentOs.Web/Orchestrations/OrchestrationModels.cs
// Phase 7b — Orchestration graph model for the drag-and-drop editor (UI layer, not part of the core Domain).
// An orchestration = a set of nodes (steps) + edges (routes) on the canvas. The editor allows building any
// graph; "Run" interprets the graph (see OrchestrationRunner).

using System.Collections.Generic;

namespace AgentOs.Web.Orchestrations;

/// <summary>Step type in an orchestration (matches the Synapse-style "Add step" palette).</summary>
public enum StepType
{
    /// <summary>Raw LLM call with a free-form prompt.</summary>
    Llm,

    /// <summary>Specialized agent (Requirement / Coding / Testing / QA / Orchestrator).</summary>
    Agent,

    /// <summary>Call an external tool/function.</summary>
    Tool,

    /// <summary>Evaluate the output then branch by route (e.g. pass / fail).</summary>
    Evaluator,

    /// <summary>Run multiple branches in parallel.</summary>
    Parallel,

    /// <summary>Merge the results of multiple branches.</summary>
    Merge,

    /// <summary>Loop a branch until a stop condition.</summary>
    Loop,

    /// <summary>Human checkpoint (human-in-the-loop).</summary>
    Human,

    /// <summary>Transform data (map/format).</summary>
    Transform,

    /// <summary>Extract JSON from text.</summary>
    ExtractJson,

    /// <summary>Binary branch by condition.</summary>
    IfElse,

    /// <summary>Branch into multiple paths by value.</summary>
    Switch,

    /// <summary>Print/log a value.</summary>
    Print,

    /// <summary>End the orchestration.</summary>
    End,
}

/// <summary>Display group (decides the card color). Derived from <see cref="StepType"/>.</summary>
public enum StepCategory
{
    /// <summary>Agent — teal.</summary>
    Agent,

    /// <summary>Evaluate/branch — purple.</summary>
    Evaluator,

    /// <summary>Human — red/maroon.</summary>
    Human,

    /// <summary>Raw LLM — blue.</summary>
    Llm,

    /// <summary>Flow logic (Loop/Merge/Parallel/Transform/ExtractJson) — gray.</summary>
    Logic,

    /// <summary>End — dark gray.</summary>
    Terminal,
}

/// <summary>Runtime state of a node while running (for realtime coloring).</summary>
public enum NodeRunState
{
    /// <summary>Not yet run.</summary>
    Idle,

    /// <summary>Running.</summary>
    Running,

    /// <summary>Done.</summary>
    Done,

    /// <summary>Error.</summary>
    Failed,

    /// <summary>Not run — the node type isn't supported by the executor yet.</summary>
    Skipped,

    /// <summary>Finished on canned offline output — no model was called (no API key + offline fallback).</summary>
    Canned,
}

/// <summary>A step on the canvas. The (X,Y) position is in canvas coordinates.</summary>
public sealed class GraphNode
{
    /// <summary>Unique id within the graph.</summary>
    public required string Id { get; set; }

    /// <summary>Step type.</summary>
    public StepType Type { get; set; }

    /// <summary>Display name shown on the card's title bar.</summary>
    public string Title { get; set; } = "Step";

    /// <summary>X coordinate on the canvas.</summary>
    public double X { get; set; }

    /// <summary>Y coordinate on the canvas.</summary>
    public double Y { get; set; }

    /// <summary>Agent role (only for <see cref="StepType.Agent"/>): Requirement/Coding/Testing/Qa/Orchestrator.</summary>
    public string? AgentRole { get; set; }

    /// <summary>Short description / prompt shown in the card body.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Input state key (shown as "in:").</summary>
    public string Input { get; set; } = string.Empty;

    /// <summary>Output state key (shown as "out:").</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>Max iterations (shown as "max Nx"). 0 ⇒ hidden.</summary>
    public int MaxIterations { get; set; }

    /// <summary>Branch routes (Evaluator/IfElse/Switch). Shown as "N routes: a, b".</summary>
    public List<string> Routes { get; set; } = [];

    /// <summary>The graph's start node (green START badge).</summary>
    public bool IsStart { get; set; }

    /// <summary>Display group derived from <see cref="Type"/> (+ role).</summary>
    public StepCategory Category => Type switch
    {
        StepType.Agent => StepCategory.Agent,
        StepType.Evaluator or StepType.IfElse or StepType.Switch => StepCategory.Evaluator,
        StepType.Human => StepCategory.Human,
        StepType.Llm => StepCategory.Llm,
        StepType.End => StepCategory.Terminal,
        _ => StepCategory.Logic,
    };
}

/// <summary>An edge connecting 2 nodes, with an optional route label.</summary>
public sealed class GraphEdge
{
    /// <summary>Unique id.</summary>
    public required string Id { get; set; }

    /// <summary>Source node.</summary>
    public required string SourceId { get; set; }

    /// <summary>Target node.</summary>
    public required string TargetId { get; set; }

    /// <summary>Route label (e.g. "continue", "pass", "fail", "ask_user"). Empty ⇒ hidden.</summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>An orchestration: metadata + graph + state schema + guardrails.</summary>
public sealed class OrchestrationGraph
{
    /// <summary>Unique id.</summary>
    public required string Id { get; set; }

    /// <summary>Display name (selector + title).</summary>
    public string Name { get; set; } = "Untitled";

    /// <summary>Short description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The nodes.</summary>
    public List<GraphNode> Nodes { get; set; } = [];

    /// <summary>The edges.</summary>
    public List<GraphEdge> Edges { get; set; } = [];

    /// <summary>State schema JSON ("State Schema" tab).</summary>
    public string StateSchemaJson { get; set; } = "{\n  \"input\": \"string\"\n}";

    /// <summary>Guardrails ("Guardrails" tab).</summary>
    public List<string> Guardrails { get; set; } = [];
}

/// <summary>One entry in a tenant's recent-workflow-runs list (session-scoped, in <see cref="OrchestrationStore"/>).</summary>
/// <param name="GraphName">The workflow that was run.</param>
/// <param name="At">When the run finished.</param>
/// <param name="Completed">True if the run reached the end; false if it stopped/failed/was cancelled.</param>
/// <param name="Detail">A short outcome line (e.g. "3 QA rounds" or the failure reason).</param>
public sealed record OrchestrationRunRecord(string GraphName, System.DateTimeOffset At, bool Completed, string Detail);
