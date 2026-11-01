// The tunable agent prompts the Prompts admin app exposes. Key must match what each agent passes to
// IPromptOverrides.ResolveAsync — the agent's role key (AgentRoles), so a prompt, a profile and an Insights row of the
// same agent share one name; DefaultPrompt is the compiled-in baseline shown for reset/diff. Board agent prompts are
// per-run templates: their placeholders ({owner}, {branch}…) are filled on every run (PromptTemplate).

using System.Collections.Generic;
using AgentOs.Modules.Pipeline.Agents;

namespace AgentOs.Modules.Pipeline.Prompts;

/// <summary>One tunable agent prompt.</summary>
public sealed record PromptEntry(string Key, string DisplayName, string DefaultPrompt)
{
    /// <summary>Board agent or Workflow pipeline agent.</summary>
    public AgentRoleGroup Group { get; init; } = AgentRoleGroup.WorkflowPipeline;

    /// <summary>Placeholders the template fills per run (empty = a plain prompt).</summary>
    public IReadOnlyList<string> Placeholders { get; init; } = [];

    /// <summary>One sentence shown under the prompt (where it applies), or null.</summary>
    public string? Note { get; init; }
}

/// <summary>The agent system prompts a tenant can override at runtime.</summary>
public static class PromptCatalog
{
    /// <summary>Tunable prompts, in display order (Board agents, then the Workflow pipeline). Keys match the agents'
    /// ResolveAsync calls.</summary>
    public static IReadOnlyList<PromptEntry> All { get; } =
    [
        new(AgentRoles.Discovery, AgentRoles.Label(AgentRoles.Discovery) + " — New idea → tickets", DecomposerPrompt.System)
        {
            Group = AgentRoleGroup.Board,
            Note = "Splits the idea's requirement into tickets. The requirement step itself uses the Requirement (Workflow pipeline) prompt.",
        },
        new(AgentRoles.IssueWork, AgentRoles.Label(AgentRoles.IssueWork) + " — on a member's computer", IssueWorkPrompt.CliTemplate)
        {
            Group = AgentRoleGroup.Board,
            Placeholders = IssueWorkPrompt.Placeholders,
            Note = "Used when the ticket runs on a paired computer (the Board default).",
        },
        new(IssueWorkPrompt.ServerPromptKey, AgentRoles.Label(AgentRoles.IssueWork) + " — on the server", IssueWorkPrompt.ServerTemplate)
        {
            Group = AgentRoleGroup.Board,
            Placeholders = IssueWorkPrompt.Placeholders,
            Note = "Used when the ticket's provider runs on the server and drives the runner through runner_shell.",
        },
        new(AgentRoles.Reviewer, AgentRoles.Label(AgentRoles.Reviewer), ReviewAgent.InstructionsTemplate)
        {
            Group = AgentRoleGroup.Board,
            Placeholders = AcceptanceAgent.Placeholders,
        },
        new(AgentRoles.Acceptance, AgentRoles.Label(AgentRoles.Acceptance), AcceptanceAgent.InstructionsTemplate)
        {
            Group = AgentRoleGroup.Board,
            Placeholders = AcceptanceAgent.Placeholders,
        },
        new(AgentRoles.Designer, AgentRoles.Label(AgentRoles.Designer) + " — the Design gate", DesignAgent.InstructionsTemplate)
        {
            Group = AgentRoleGroup.Board,
            Placeholders = AcceptanceAgent.Placeholders,
            Note = "Checks a UI change's screenshots (390 / 1280 px, light / dark) when the ticket flow runs Design.",
        },
        new(AgentRoles.AutopilotOrchestrator, AgentRoles.Label(AgentRoles.AutopilotOrchestrator), AutopilotOrchestratorAgent.Instructions)
        {
            Group = AgentRoleGroup.Board,
        },
        new(AgentRoles.Retrospective, AgentRoles.Label(AgentRoles.Retrospective), RetrospectiveAgent.Instructions)
        {
            Group = AgentRoleGroup.Board,
        },
        new(AgentRoles.Requirement, AgentRoles.Label(AgentRoles.Requirement), RequirementPrompt.System),
        new(AgentRoles.Coding, AgentRoles.Label(AgentRoles.Coding), CodingPrompt.System),
        new(AgentRoles.Testing, AgentRoles.Label(AgentRoles.Testing), TestingPrompt.System),
        new(AgentRoles.Qa, AgentRoles.Label(AgentRoles.Qa), QaPrompt.System),
    ];
}
