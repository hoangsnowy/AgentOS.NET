// A failed Board task, explained for someone who isn't a developer (docs/plans/non-tech-ux.md #1).
//
// Raw agent/transport errors ("No paired runner connected for member '…' in tenant '…'", "exit -1:", an agent's
// three-paragraph apology) were shown verbatim in 11px red text. Classify the stored error into a handful of kinds
// a person can act on, each with one sentence and ONE primary action; the raw text stays one click away.
// Pure + ordered (first match wins) so the mapping is unit-tested; a new failure text is a new row here.

using System;

namespace AgentOs.Web.Services;

/// <summary>What the member can do about a failure — the one primary button on the card.</summary>
public enum TaskFailureAction
{
    /// <summary>Run the task again (most failures are worth one more try).</summary>
    TryAgain,
    /// <summary>Settings › AI › Model keys.</summary>
    ConnectAiAccount,
    /// <summary>Board › Computers.</summary>
    ConnectComputer,
    /// <summary>Board › Projects (reconnect GitHub).</summary>
    ReconnectGitHub,
    /// <summary>Insights › budget.</summary>
    RaiseSpendingLimit,
}

/// <summary>The explained failure: a short headline, one sentence of what to do, and the action button.</summary>
public sealed record TaskFailureInfo(string Kind, string Headline, string Advice, TaskFailureAction Action, string ActionLabel);

/// <summary>Maps a stored task/repo error to <see cref="TaskFailureInfo"/>.</summary>
public static class TaskFailure
{
    private static readonly (string Kind, string[] Needles, string Headline, string Advice, TaskFailureAction Action, string Label)[] Rules =
    [
        ("stopped", ["Stopped — you pressed Stop", "Stopped by you"],
            "You stopped this task", "Nothing more will happen. Try again whenever you're ready.", TaskFailureAction.TryAgain, "Try again"),
        ("no-ai-account", ["API key configured", "Model keys", "LLM client init failed", "No API key"],
            "No AI account is connected", "Connect an AI account (Anthropic, OpenAI…) in Settings, then try again.",
            TaskFailureAction.ConnectAiAccount, "Connect an AI account"),
        ("computer-offline", ["No paired runner connected", "No remote dev runner connected", "runner is offline"],
            "Your computer isn't connected", "Open the AgentOS.NET helper on your computer (or connect one), then try again.",
            TaskFailureAction.ConnectComputer, "Connect a computer"),
        ("github-access", ["rejected the PAT", "Bad credentials", "board credentials", "Reconnect the board"],
            "GitHub access has expired", "Reconnect the project to GitHub, then try again.", TaskFailureAction.ReconnectGitHub, "Reconnect GitHub"),
        ("spending-limit", ["budget exceeded", "monthly LLM budget"],
            "The monthly spending limit is reached", "Raise the limit, or wait for next month.", TaskFailureAction.RaiseSpendingLimit, "Change the limit"),
        ("needs-permission", ["needs approval", "needed approval", "was not approved", "requires approval", "Refused by the runner", "Denied by approval gate"],
            "The AI needed a permission it doesn't have", "Your computer's helper blocked a step. Update the helper, then try again.",
            TaskFailureAction.TryAgain, "Try again"),
        ("timeout", ["timed out", "did not respond within"],
            "It took too long", "The AI ran out of time. Big tasks can — try again, or split the ticket.", TaskFailureAction.TryAgain, "Try again"),
        ("build", ["build failed", "Build FAILED", "error CS", "error TS", "npm ERR"],
            "The code didn't build", "The AI can read the errors and try again.", TaskFailureAction.TryAgain, "Fix it for me"),
        ("tests", ["tests failed", "test failed", "Test run failed"],
            "Some tests failed", "The AI can look at the failing tests and try again.", TaskFailureAction.TryAgain, "Fix it for me"),
        ("not-greenfield", ["Greenfield", "greenfield"],
            "Thorough mode only builds new features", "Use Fast mode for changes to existing code (Advanced › How thorough).",
            TaskFailureAction.TryAgain, "Try again"),
    ];

    private static readonly TaskFailureInfo Unknown = new("unknown", "The AI couldn't finish this task",
        "See the details below. Trying again often works.", TaskFailureAction.TryAgain, "Try again");

    /// <summary>Explain <paramref name="error"/>; null/blank → null (nothing failed).</summary>
    public static TaskFailureInfo? Explain(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) { return null; }
        foreach (var r in Rules)
        {
            foreach (var needle in r.Needles)
            {
                if (error.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return new TaskFailureInfo(r.Kind, r.Headline, r.Advice, r.Action, r.Label);
                }
            }
        }
        return Unknown;
    }
}
