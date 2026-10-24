// AgentOs.Application/Agents/AgentsOptions.cs
// Seed defaults for every agent profile (the "Agents" section; appsettings is bootstrap-only — ADR-0011). Property
// names ARE the role keys of AgentRoles, so Agents:<Key>:* binds here and seeds Settings → Agent profiles.

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>
/// Per-agent configuration — bound from the <c>"Agents"</c> section. One property per <see cref="AgentRoles"/> key.
/// </summary>
public sealed class AgentsOptions
{
    /// <summary>Section name for <c>Configuration.GetSection</c>.</summary>
    public const string SectionName = "Agents";

    // The Board agents that need a real machine (a checkout, a browser, the repo's toolchain) or answer from one run
    // on the member's paired computer by default — 0 server tokens.
    private const string MembersComputer = "RemoteAgent";

    // Board agents choose NO model by default (ComputersDefault): on a member's computer the CLI runs its own model (the
    // member's choice, usually newer than any id we would pin); on our servers the provider's configured model applies
    // (Settings → Model keys / Llm:<Provider>:Model, resolved in the LLM gateway). Picking a model in Agent profiles
    // passes it explicitly (claude --model / codex -m on a computer).
    private const string ComputersDefault = "";

    /// <summary>Workflow pipeline — Orchestrator configuration.</summary>
    public AgentOptions Orchestrator { get; set; } = new() { Model = "claude-haiku-4-5", Temperature = 0.3, MaxTokens = 2000 };

    /// <summary>Workflow pipeline — RequirementAgent configuration.</summary>
    public AgentOptions Requirement { get; set; } = new() { Model = "claude-sonnet-4-6", Temperature = 0.1, MaxTokens = 2000 };

    /// <summary>Workflow pipeline — CodingAgent configuration.</summary>
    public AgentOptions Coding { get; set; } = new() { Provider = "AzureOpenAI", Model = "gpt-4.1", Temperature = 0.2, MaxTokens = 4000 };

    /// <summary>Workflow pipeline — TestingAgent configuration.</summary>
    public AgentOptions Testing { get; set; } = new() { Provider = "AzureOpenAI", Model = "gpt-4o-mini", Temperature = 0.2, MaxTokens = 3000 };

    /// <summary>Workflow pipeline — QaAgent configuration.</summary>
    public AgentOptions Qa { get; set; } = new() { Model = "claude-haiku-4-5", Temperature = 0.1, MaxTokens = 1500 };

    /// <summary>Board — Coding (tickets): IssueWorkAgent, the agentic issue→branch run.</summary>
    public AgentOptions IssueWork { get; set; } = new() { Model = ComputersDefault, Temperature = 0.2, MaxTokens = 32768 };

    /// <summary>Board — Discovery (New idea): the requirement step and the ticket split (TicketDecomposerAgent).</summary>
    public AgentOptions Discovery { get; set; } = new() { Model = ComputersDefault, Temperature = 0.2, MaxTokens = 4000 };

    /// <summary>Board — Reviewer (ReviewAgent). Reads the change in a checkout on the member's computer.</summary>
    public AgentOptions Reviewer { get; set; } = new() { Provider = MembersComputer, Model = ComputersDefault, Temperature = 0, MaxTokens = 32768 };

    /// <summary>Board — Testing (tickets): AcceptanceAgent. Builds and tests the change on the member's computer.</summary>
    public AgentOptions Acceptance { get; set; } = new() { Provider = MembersComputer, Model = ComputersDefault, Temperature = 0, MaxTokens = 32768 };

    /// <summary>Board — Autopilot lead (AutopilotOrchestratorAgent). Answer-only.</summary>
    public AgentOptions AutopilotOrchestrator { get; set; } = new() { Provider = MembersComputer, Model = ComputersDefault, Temperature = 0, MaxTokens = 4096 };

    /// <summary>Board — Retrospective (RetrospectiveAgent). Answer-only.</summary>
    public AgentOptions Retrospective { get; set; } = new() { Provider = MembersComputer, Model = ComputersDefault, Temperature = 0, MaxTokens = 4096 };

    /// <summary>True when <paramref name="role"/> may leave its model empty ("the computer's default"): the Board agents.</summary>
    public static bool ModelIsOptional(string role) => AgentRoles.Find(role)?.Group == AgentRoleGroup.Board;

    /// <summary>The seed profile for <paramref name="role"/> (an <see cref="AgentRoles"/> key), or null when unknown.</summary>
    public AgentOptions? For(string role) => role switch
    {
        AgentRoles.Orchestrator => Orchestrator,
        AgentRoles.Requirement => Requirement,
        AgentRoles.Coding => Coding,
        AgentRoles.Testing => Testing,
        AgentRoles.Qa => Qa,
        AgentRoles.IssueWork => IssueWork,
        AgentRoles.Discovery => Discovery,
        AgentRoles.Reviewer => Reviewer,
        AgentRoles.Acceptance => Acceptance,
        AgentRoles.AutopilotOrchestrator => AutopilotOrchestrator,
        AgentRoles.Retrospective => Retrospective,
        _ => null,
    };

    /// <summary>Startup validation: every agent has a provider, Temperature in [0, 2] and MaxTokens &gt; 0; every
    /// Workflow-pipeline agent has a model (a Board agent may leave it empty — see <see cref="ModelIsOptional"/>).</summary>
    public bool IsValid() => AgentRoles.All.All(r => For(r.Key) is { } a
        && !string.IsNullOrWhiteSpace(a.Provider)
        && (ModelIsOptional(r.Key) || !string.IsNullOrWhiteSpace(a.Model))
        && a.Temperature is >= 0 and <= 2
        && a.MaxTokens > 0);

    /// <summary>Every configured agent, for cross-cutting checks (startup validation).</summary>
    public IEnumerable<AgentOptions> All() => AgentRoles.All.Select(role => For(role.Key)).OfType<AgentOptions>();
}

/// <summary>Configuration for a single agent.</summary>
public sealed class AgentOptions
{
    /// <summary>Provider name: <c>Anthropic</c> / <c>AzureOpenAI</c>.</summary>
    public string Provider { get; set; } = "Anthropic";

    /// <summary>Model alias.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Sampling temperature [0, 2].</summary>
    public double Temperature { get; set; }

    /// <summary>Output max tokens.</summary>
    public int MaxTokens { get; set; } = 2000;

    /// <summary>Max repos run concurrently by the multi-repo IssueWorkAgent. Bounds fan-out so a
    /// many-repo session doesn't overwhelm the paired dev machine. Only the IssueWork agent reads it.
    /// Clamped to ≥1 at use; 1 = the old sequential behaviour.</summary>
    public int MaxParallelRepos { get; set; } = 3;
}
