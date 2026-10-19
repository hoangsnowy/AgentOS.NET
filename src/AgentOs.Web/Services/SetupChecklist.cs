// UX phase 2 (docs/plans/ux-simplification.md): the Home setup checklist. Every step is computed from REAL state
// (a key saved, a run recorded, a repo connected, a budget set, a machine paired) — never a tutorial that asks
// people to click through cards (NN/g: contextual help over deck-of-cards onboarding). Each step deep-links to
// the one place that completes it. Optional steps never block "done".

using System.Collections.Generic;
using System.Linq;

namespace AgentOs.Web.Services;

/// <summary>What the checklist reads, gathered once per Home load.</summary>
public sealed record SetupFacts(bool HasModelKey, bool HasRun, bool HasRepository, bool HasBudget, bool HasPairedMachine);

/// <summary>Where a step's button goes.</summary>
public enum SetupAction
{
    /// <summary>Settings → AI → Model keys.</summary>
    ModelKeys,
    /// <summary>Focus the Home composer.</summary>
    Composer,
    /// <summary>The Board app (connect a repo / pair a machine).</summary>
    Board,
    /// <summary>The Insights app (budget).</summary>
    Insights,
}

/// <summary>One checklist step.</summary>
public sealed record SetupStep(string Id, string Title, string Hint, bool Done, bool Optional, SetupAction Action, string ActionLabel);

/// <summary>Builds the checklist from facts.</summary>
public static class SetupChecklist
{
    /// <summary>The steps in order.</summary>
    public static IReadOnlyList<SetupStep> Build(SetupFacts facts)
    {
        System.ArgumentNullException.ThrowIfNull(facts);
        return
        [
            new("key", "Add a model key", "Anthropic, Azure OpenAI, OpenAI, Gemini or xAI — then Test it.", facts.HasModelKey, false, SetupAction.ModelKeys, "Add key"),
            new("run", "Run the agents once", "Describe a small change above and press Run now.", facts.HasRun, false, SetupAction.Composer, "Write a story"),
            new("repo", "Connect a repository", "So the agents can open pull requests on your code.", facts.HasRepository, false, SetupAction.Board, "Open Board"),
            new("budget", "Set a monthly budget", "A cap the agents can't spend past.", facts.HasBudget, false, SetupAction.Insights, "Set budget"),
            new("pair", "Pair a dev machine", "Run on your own Claude Code / Copilot CLI with no server tokens.", facts.HasPairedMachine, true, SetupAction.Board, "Pair"),
        ];
    }

    /// <summary>Required steps done / required steps total.</summary>
    public static (int Done, int Total) Progress(IReadOnlyList<SetupStep> steps)
    {
        System.ArgumentNullException.ThrowIfNull(steps);
        var required = steps.Where(s => !s.Optional).ToList();
        return (required.Count(s => s.Done), required.Count);
    }

    /// <summary>True when every required step is done (the checklist then hides itself).</summary>
    public static bool IsComplete(IReadOnlyList<SetupStep> steps) => Progress(steps) is var (done, total) && done == total;
}
