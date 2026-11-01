// A run on this computer that ended without the reply its prompt asks for — the CLI ran out of turns or context after
// doing real work (commits made, maybe pushed) — is asked once for that reply instead of being thrown away. The server
// sends a short follow-up naming the run (RemoteExecRequest.ResumeOf). So the follow-up can look at the same checkout
// and, for Claude Code, continue the same conversation (`claude -p --resume <session id>`), the runner keeps the folder
// (and session id) of a run whose reply looked unfinished for a short while instead of deleting it straight away.
// Anything not asked for within that while is deleted on the next run. Pure state + rules, so it is unit-tested.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentOs.RemoteAgent;

/// <summary>Run folders kept for one follow-up call.</summary>
internal sealed class RunContinuation
{
    /// <summary>How long a kept folder waits for its follow-up.</summary>
    internal static readonly TimeSpan Keep = TimeSpan.FromMinutes(15);

    /// <summary>The runner's one set of kept runs.</summary>
    internal static RunContinuation Shared { get; } = new();

    /// <summary>A kept run: its folder, the CLI session it ran in (null when the CLI reported none) and whether it
    /// stopped because its context was full (resuming that conversation would fail the same way).</summary>
    internal sealed record Parked(string Folder, string? SessionId, bool ContextFull, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Parked> _runs = new(StringComparer.Ordinal);

    /// <summary>How many runs are kept right now.</summary>
    internal int Count => _runs.Count;

    /// <summary>A reply that holds no JSON object at all can't be the answer the prompt asked for.</summary>
    internal static bool LooksUnfinished(string? reply) => string.IsNullOrWhiteSpace(reply) || !reply.Contains('{', StringComparison.Ordinal);

    /// <summary>The CLI stopped before it answered: it reached its turn limit, or its context was full.</summary>
    internal static bool StoppedShort(CliProgress.Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Equals(result.Subtype, "error_max_turns", StringComparison.Ordinal) || ContextFull(result);
    }

    /// <summary>The run ended because its conversation no longer fit the model's context.</summary>
    internal static bool ContextFull(CliProgress.Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsError && (result.Text.Contains("Prompt is too long", StringComparison.OrdinalIgnoreCase)
            || result.Text.Contains("context window", StringComparison.OrdinalIgnoreCase)
            || result.Text.Contains("context length", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The line the reply carries in place of the answer the run never gave.</summary>
    internal static string StoppedNote(CliProgress.Result result) => ContextFull(result)
        ? "[AgentOS.NET runner] The run stopped before it replied: its context was full."
        : "[AgentOS.NET runner] The run stopped before it replied: it reached its turn limit.";

    /// <summary>Keep a run's folder for its follow-up. A session id that isn't a plain id is dropped (it becomes a
    /// command-line argument).</summary>
    internal void Park(string requestId, string folder, string? sessionId, bool contextFull, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var session = sessionId is { Length: > 0 and <= 64 } s && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && s[0] != '-'
            ? s
            : null;
        _runs[requestId] = new Parked(folder, session, contextFull, now);
    }

    /// <summary>The kept run a follow-up continues — handed out once — or null (never kept, expired, its folder gone).</summary>
    internal Parked? Take(string? requestId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(requestId) || !_runs.TryRemove(requestId, out var parked)) { return null; }
        return now - parked.At <= Keep && Directory.Exists(parked.Folder) ? parked : null;
    }

    /// <summary>Forget the runs whose follow-up never came and return their folders, to delete.</summary>
    internal IReadOnlyList<string> Expired(DateTimeOffset now)
    {
        var gone = new List<string>();
        foreach (var (id, parked) in _runs)
        {
            if (now - parked.At > Keep && _runs.TryRemove(id, out var removed)) { gone.Add(removed.Folder); }
        }
        return gone;
    }

    /// <summary>The flags that continue the kept run's conversation: <c>--resume &lt;session&gt;</c> for Claude Code when
    /// it reported a session and its context was not full; nothing otherwise (the follow-up then starts a fresh
    /// conversation in the same folder, and its prompt carries the end of what the run said).</summary>
    internal static string[] ResumeArgs(string command, Parked? parked)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (parked is not { SessionId: { } session, ContextFull: false }) { return []; }
        var name = Path.GetFileNameWithoutExtension(command[(command.LastIndexOfAny(['/', '\\']) + 1)..]);
        return string.Equals(name, "claude", StringComparison.OrdinalIgnoreCase) ? ["--resume", session] : [];
    }
}
