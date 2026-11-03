// The model an agent's profile picked (Settings → Agent profiles) reaches the CLI. The server sends it on every request;
// the runner used to log it and run the CLI's own default model anyway. Claude Code takes `--model <id>`, Codex takes
// `-m <id>`. No model, a Claude model for codex, an unknown CLI, or an operator's REMOTE_AGENT_ARGS that already names a
// model → no flag, the CLI's default applies. A non-Claude model for claude (a profile set to gpt-4.1 run on a member's
// Claude Code) gets the configured Claude fallback (REMOTE_AGENT_CLAUDE_FALLBACK_MODEL, default "sonnet") instead of the
// CLI default: the default on a member's machine may be a slow thinking model, and Workflow Coding steps on it ran past
// the answer deadline (lab, 2026-11-04). An empty fallback keeps the CLI default.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentOs.RemoteAgent;

/// <summary>Builds the model flag for a CLI agent.</summary>
internal static partial class CliModel
{
    /// <summary>The Claude model a non-Claude profile model runs as on claude (an alias Claude Code resolves).</summary>
    internal const string DefaultClaudeFallback = "sonnet";

    /// <summary>The runner's environment variable that overrides <see cref="DefaultClaudeFallback"/> (empty = CLI default).</summary>
    internal const string ClaudeFallbackVariable = "REMOTE_AGENT_CLAUDE_FALLBACK_MODEL";

    // Claude Code's model aliases (besides full claude-* ids).
    private static readonly HashSet<string> ClaudeAliases = new(StringComparer.OrdinalIgnoreCase) { "opus", "sonnet", "haiku", "fable", "opusplan" };

    /// <summary>The arguments that select <paramref name="model"/> on <paramref name="command"/> (empty = none).</summary>
    /// <param name="command">The CLI command or path (<c>claude</c>, <c>C:\…\codex.cmd</c>).</param>
    /// <param name="model">The model id the request carries (may be empty).</param>
    /// <param name="existingArgs">The arguments already chosen; one that names a model wins.</param>
    /// <param name="claudeFallback">The Claude model claude runs when <paramref name="model"/> is not a Claude model
    /// (empty = the CLI's default).</param>
    internal static string[] Flags(string command, string? model, IReadOnlyCollection<string> existingArgs,
        string? claudeFallback = DefaultClaudeFallback)
    {
        ArgumentNullException.ThrowIfNull(existingArgs);
        var id = (model ?? string.Empty).Trim();
        if (id.Length == 0 || !SafeId().IsMatch(id)) { return []; }
        if (existingArgs.Any(a => a is "--model" or "-m" || a.StartsWith("--model=", StringComparison.Ordinal))) { return []; }

        var isClaudeModel = id.StartsWith("claude", StringComparison.OrdinalIgnoreCase) || ClaudeAliases.Contains(id);
        var fallback = (claudeFallback ?? string.Empty).Trim();
        return CommandName(command) switch
        {
            "claude" when isClaudeModel => ["--model", id],
            "claude" when fallback.Length > 0 && SafeId().IsMatch(fallback) => ["--model", fallback],
            "codex" when !isClaudeModel => ["-m", id],
            _ => [],
        };
    }

    /// <summary>The CLI's bare name: <c>C:\npm\claude.cmd</c> → <c>claude</c>.</summary>
    internal static string CommandName(string command)
    {
        // Both separators on every OS: REMOTE_AGENT_CMD may hold a Windows path while the tests run on Linux.
        var name = (command ?? string.Empty).Trim();
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).ToLowerInvariant();
    }

    // A model id, never something that could read as another flag or carry a separator.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/\[\]-]{0,127}$")]
    private static partial Regex SafeId();
}
