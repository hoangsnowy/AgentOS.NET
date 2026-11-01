// A run on this computer that ended without the reply its prompt asks for — the CLI ran out of turns or context after
// doing real work (commits made, maybe pushed) — is asked once for that reply instead of being thrown away. The server
// sends a short follow-up naming the run (RemoteExecRequest.ResumeOf). So the follow-up can look at the same checkout
// and, for Claude Code, continue the same conversation (`claude -p --resume <session id>`), the runner keeps the folder
// (and session id) of a run whose reply looked unfinished for a short while instead of deleting it straight away.
// Only a run the SERVER asked to have hands (RemoteExecRequest.Autonomy "scoped") is kept — never an answer-only request
// on a machine that grants scoped autonomy to everything. A kept folder carries a "<folder>.parked" marker beside it (not
// inside the checkout): a sweep every minute deletes what was not asked for within the keep window, the runner deletes
// every kept folder when it stops, and the next start deletes the leftovers of one that crashed. REMOTE_AGENT_KEEP_RUNS
// (keep every run folder for debugging) turns keeping-for-a-follow-up off: nothing is parked, a follow-up starts fresh.
// Pure state + rules, so it is unit-tested.

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

    /// <summary>How often the runner sweeps kept folders whose follow-up never came.</summary>
    internal static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    /// <summary>The suffix of the marker file written beside a kept folder.</summary>
    internal const string MarkerSuffix = ".parked";

    /// <summary>The runner's one set of kept runs.</summary>
    internal static RunContinuation Shared { get; } = new();

    /// <summary>A kept run: its folder, the CLI session it ran in (null when the CLI reported none) and whether it
    /// stopped because its context was full (resuming that conversation would fail the same way).</summary>
    internal sealed record Parked(string Folder, string? SessionId, bool ContextFull, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Parked> _runs = new(StringComparer.Ordinal);

    /// <summary>How many runs are kept right now.</summary>
    internal int Count => _runs.Count;

    /// <summary>The server asked this request to have hands (a per-run folder) — the per-request value, not the machine's
    /// grant: only such a run may be kept for a follow-up or reported as "stopped short".</summary>
    internal static bool AskedForHands(string? autonomy) => string.Equals(autonomy?.Trim(), "scoped", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The kept run a follow-up continues — handed out once — or null (never kept, expired, its folder gone). An
    /// expired one's folder is deleted here, not left behind.</summary>
    internal Parked? Take(string? requestId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(requestId) || !_runs.TryRemove(requestId, out var parked)) { return null; }
        if (now - parked.At > Keep)
        {
            Discard(parked.Folder);
            return null;
        }
        Unmark(parked.Folder);
        return Directory.Exists(parked.Folder) ? parked : null;
    }

    /// <summary>Forget the runs whose follow-up never came and return their folders, to <see cref="Discard"/>.</summary>
    internal IReadOnlyList<string> Expired(DateTimeOffset now) =>
        Remove(_runs.Where(r => now - r.Value.At > Keep).Select(r => r.Key));

    /// <summary>Forget every kept run and return the folders, to <see cref="Discard"/> (the runner is stopping: no follow-up
    /// can reach it any more).</summary>
    internal IReadOnlyList<string> TakeAll() => Remove(_runs.Keys);

    // Forget these runs and return their folders. The ids are listed first (a snapshot), so removing while reading
    // the dictionary is safe; a run another caller took meanwhile is simply not returned.
    private List<string> Remove(IEnumerable<string> ids) =>
        [.. ids.ToList().Select(id => _runs.TryRemove(id, out var removed) ? removed.Folder : null).OfType<string>()];

    /// <summary>The marker file beside a kept folder.</summary>
    internal static string MarkerFor(string folder) => folder.TrimEnd('/', '\\') + MarkerSuffix;

    /// <summary>Write the marker beside a folder being kept, so a runner that crashes leaves something the next start can
    /// find and delete. Best-effort.</summary>
    internal static void Mark(string folder)
    {
        try { File.WriteAllText(MarkerFor(folder), string.Empty); }
        catch (IOException) { /* the in-memory sweep still deletes it */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }

    /// <summary>Delete a kept folder and its marker. Never throws.</summary>
    internal static void Discard(string folder)
    {
        ScopedAutonomy.TryDelete(folder);
        Unmark(folder);
    }

    private static void Unmark(string folder)
    {
        try { File.Delete(MarkerFor(folder)); }
        catch (IOException) { /* the next start's sweep tries again */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }

    /// <summary>At start: delete the kept folders a previous runner left behind (it crashed before its own cleanup) —
    /// every folder under <paramref name="root"/> with a marker older than <see cref="Keep"/>. Only marked folders: an
    /// unmarked one may be another runner's run in progress. Returns how many went. Never throws.</summary>
    internal static int SweepLeftovers(string root, DateTimeOffset now)
    {
        var swept = 0;
        try
        {
            if (!Directory.Exists(root)) { return 0; }
            foreach (var marker in Directory.EnumerateFiles(root, "*" + MarkerSuffix)
                         .Where(m => now - File.GetLastWriteTimeUtc(m) > Keep).ToList())
            {
                Discard(marker[..^MarkerSuffix.Length]);
                swept++;
            }
        }
        catch (IOException) { /* the next start tries again */ }
        catch (UnauthorizedAccessException) { /* same */ }
        return swept;
    }

    /// <summary>The flags that continue the kept run's conversation: <c>--resume &lt;session&gt;</c> for Claude Code when
    /// it reported a session and its context was not full; nothing otherwise (the follow-up then starts a fresh
    /// conversation in the same folder, and its prompt carries the end of what the run said). Non-empty is exactly
    /// when the follow-up's reply counts as resumed (RemoteExecResult.Resumed).</summary>
    internal static string[] ResumeArgs(string command, Parked? parked)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (parked is not { SessionId: { } session, ContextFull: false }) { return []; }
        var name = Path.GetFileNameWithoutExtension(command[(command.LastIndexOfAny(['/', '\\']) + 1)..]);
        return string.Equals(name, "claude", StringComparison.OrdinalIgnoreCase) ? ["--resume", session] : [];
    }
}
