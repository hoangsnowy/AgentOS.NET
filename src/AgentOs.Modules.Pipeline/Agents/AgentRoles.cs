// One agent registry. Every agent AgentOS.NET runs has ONE stable role key, used everywhere the agent is named:
// its profile (Settings → Agent profiles, Agents:<Key>:*), its prompt (Settings → Prompts, prompt/<Key>/system), the
// AgentRole on its LLM requests (what Insights groups by) and the label of its stored runs. The Board agents (tickets,
// reviews, testing, Autopilot, retrospectives, New idea) are listed apart from the legacy 5-agent Workflow pipeline so
// a person never edits "Coding" expecting the ticket agent to change.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Which surface an agent serves.</summary>
public enum AgentRoleGroup
{
    /// <summary>The Board: tickets, reviews, testing, Autopilot, retrospectives, New idea.</summary>
    Board,

    /// <summary>The 5-agent Workflow pipeline (Run / Workflow app, the Thorough engine).</summary>
    WorkflowPipeline,
}

/// <summary>One agent role: its stable key, the name people see, its surface and what it does.</summary>
/// <param name="Key">Stable key: profile key, prompt key, Insights AgentRole and run label.</param>
/// <param name="Label">The name shown in Agent profiles, Prompts and Insights.</param>
/// <param name="Group">Board agent or Workflow pipeline agent.</param>
/// <param name="Description">One sentence: what the agent does.</param>
public sealed record AgentRoleInfo(string Key, string Label, AgentRoleGroup Group, string Description);

/// <summary>The registry of agent roles (keys are stable; never rename one — stored settings and metrics use it).</summary>
public static class AgentRoles
{
    /// <summary>Workflow pipeline — the orchestrator.</summary>
    public const string Orchestrator = "Orchestrator";

    /// <summary>Workflow pipeline — requirement analysis.</summary>
    public const string Requirement = "Requirement";

    /// <summary>Workflow pipeline — code generation.</summary>
    public const string Coding = "Coding";

    /// <summary>Workflow pipeline — test generation.</summary>
    public const string Testing = "Testing";

    /// <summary>Workflow pipeline — QA scoring.</summary>
    public const string Qa = "Qa";

    /// <summary>Board — New idea: requirement + ticket split.</summary>
    public const string Discovery = "Discovery";

    /// <summary>Board — implements a ticket (Coding agent of the Board).</summary>
    public const string IssueWork = "IssueWork";

    /// <summary>Board — reviews a proposed change.</summary>
    public const string Reviewer = "Reviewer";

    /// <summary>Board — tests a proposed change against its acceptance criteria (Testing agent of the Board).</summary>
    public const string Acceptance = "Acceptance";

    /// <summary>Board — Autopilot's lead: decides what happens next on a board.</summary>
    public const string AutopilotOrchestrator = "AutopilotOrchestrator";

    /// <summary>Board — turns lessons into the project's practices.</summary>
    public const string Retrospective = "Retrospective";

    /// <summary>Every role: Board agents first (in the order a ticket meets them), then the Workflow pipeline.</summary>
    public static IReadOnlyList<AgentRoleInfo> All { get; } =
    [
        new(Discovery, "Discovery", AgentRoleGroup.Board, "New idea: turns an idea into a requirement and right-sized tickets."),
        new(IssueWork, "Coding (tickets)", AgentRoleGroup.Board, "Implements a ticket: clone, change, build, test, push a branch."),
        new(Reviewer, "Reviewer", AgentRoleGroup.Board, "Reviews a proposed change before it is tested; must-fix findings go back to Coding."),
        new(Acceptance, "Testing (tickets)", AgentRoleGroup.Board, "Tests a proposed change against its ticket's acceptance criteria."),
        new(AutopilotOrchestrator, "Autopilot lead", AgentRoleGroup.Board, "Decides what Autopilot does next on a board: starts, retries, questions, closes."),
        new(Retrospective, "Retrospective", AgentRoleGroup.Board, "After a merge, turns lessons into the project's practices."),
        new(Orchestrator, "Orchestrator (Workflow pipeline)", AgentRoleGroup.WorkflowPipeline, "Coordinates the 5-agent Workflow pipeline."),
        new(Requirement, "Requirement (Workflow pipeline)", AgentRoleGroup.WorkflowPipeline, "Turns a user story into a requirement spec."),
        new(Coding, "Coding (Workflow pipeline)", AgentRoleGroup.WorkflowPipeline, "Writes code for a requirement spec."),
        new(Testing, "Testing (Workflow pipeline)", AgentRoleGroup.WorkflowPipeline, "Writes tests for the generated code."),
        new(Qa, "QA (Workflow pipeline)", AgentRoleGroup.WorkflowPipeline, "Scores requirement, code and test consistency."),
    ];

    private static readonly FrozenDictionary<string, AgentRoleInfo> ByKey =
        All.ToFrozenDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

    // Names stored before the registry existed, mapped to today's key: the pipeline agents file metrics under their type
    // name ("CodingAgent"), the decomposer used its own name, and the Board's Testing runs were labelled "Testing".
    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Decomposer"] = Discovery,
        ["TicketDecomposer"] = Discovery,
        ["TicketDecomposerAgent"] = Discovery,
        ["IssueWorkAgent"] = IssueWork,
        ["ReviewAgent"] = Reviewer,
        ["AcceptanceAgent"] = Acceptance,
        ["AutopilotOrchestratorAgent"] = AutopilotOrchestrator,
        ["RetrospectiveAgent"] = Retrospective,
        ["PipelineOrchestrator"] = Orchestrator,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The role for <paramref name="key"/>, or null when it is not a registered role.</summary>
    public static AgentRoleInfo? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var info) ? info : null;

    /// <summary>The registered key for a stored agent name ("CodingAgent" → "Coding", "Decomposer" → "Discovery"); an
    /// unknown name comes back trimmed, without a trailing "Agent".</summary>
    public static string Canonical(string? agentName)
    {
        var name = (agentName ?? string.Empty).Trim();
        if (name.Length == 0) { return name; }
        if (Aliases.TryGetValue(name, out var alias)) { return alias; }
        if (ByKey.TryGetValue(name, out var exact)) { return exact.Key; }
        var stripped = name.EndsWith("Agent", StringComparison.Ordinal) && name.Length > "Agent".Length ? name[..^"Agent".Length].Trim() : name;
        return ByKey.TryGetValue(stripped, out var info) ? info.Key : stripped;
    }

    /// <summary>The name people see for a stored agent name (the registry label, or the name itself).</summary>
    public static string Label(string? agentName)
    {
        var key = Canonical(agentName);
        return ByKey.TryGetValue(key, out var info) ? info.Label : key;
    }
}
