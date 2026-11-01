// Governance of a CLI run on a member's computer: every tool call the CLI made (Bash, Edit, Write, Read…) becomes one
// evidence row on the server, like a tool call made through the server's gateway. This folds the stream-json events
// CliProgress reads into finished calls — tool, bounded + redacted summary, ok / error / refused by the CLI's
// permissions, start time, duration — and hands them out in batches the runner ships over the hub
// (ReportToolCalls) during the run, plus a final batch before CompleteRequest. A run that is stopped still reports
// what it did: the last batch goes out after the CLI was killed, and the server accepts it for a short grace window.
// Not thread-safe: one reader loop per run owns it.

using System;
using System.Collections.Generic;

namespace AgentOs.RemoteAgent;

/// <summary>One tool call the CLI made, as it travels to the server (must match the server's <c>RunnerToolEvent</c>).</summary>
/// <param name="ToolUseId">The CLI's id of the call (<c>toolu_…</c>).</param>
/// <param name="Tool">The CLI tool (<c>Bash</c>, <c>Edit</c>, an MCP tool…).</param>
/// <param name="Summary">What it did, bounded and redacted (the command, the file's path) — never contents or output.</param>
/// <param name="Ok">True when the tool ran and reported no error.</param>
/// <param name="Denied">True when the CLI's permissions refused it (it never ran).</param>
/// <param name="StartedUtc">When the CLI asked for it.</param>
/// <param name="DurationMs">Until its result came back (0 for a refusal reported only at the end).</param>
internal sealed record RunnerToolEvent(string ToolUseId, string Tool, string Summary, bool Ok, bool Denied, DateTimeOffset StartedUtc, long DurationMs);

/// <summary>Collects the tool calls of one CLI run.</summary>
internal sealed class CliToolCalls
{
    /// <summary>The most calls one run itemises; the rest are counted in one closing row.</summary>
    internal const int MaxPerRun = 2000;

    /// <summary>A batch goes out once this many calls finished…</summary>
    internal const int BatchSize = 25;

    /// <summary>…or this long after the last one went out.</summary>
    internal static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, (string Tool, string Summary, DateTimeOffset Started)> _open = new(StringComparer.Ordinal);
    private readonly List<RunnerToolEvent> _ready = [];
    // Every id already finished (sent or waiting), so a closing permission_denials entry is never counted twice.
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private DateTimeOffset _lastFlush;
    private int _recorded;
    private int _overflow;

    /// <summary>Starts the flush clock at <paramref name="now"/>.</summary>
    internal CliToolCalls(DateTimeOffset now) => _lastFlush = now;

    /// <summary>Calls finished and not yet taken.</summary>
    internal int Pending => _ready.Count;

    /// <summary>Fold one stream-json event observed at <paramref name="now"/>.</summary>
    internal void Observe(CliProgress.Event e, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(e);
        foreach (var use in e.ToolUses)
        {
            if (!_finished.Contains(use.Id)) { _open.TryAdd(use.Id, (use.Tool, use.Summary, now)); }
        }
        foreach (var outcome in e.Outcomes)
        {
            if (_open.Remove(outcome.ToolUseId, out var call))
            {
                Finish(new RunnerToolEvent(outcome.ToolUseId, call.Tool, call.Summary, !outcome.IsError, outcome.Denied, call.Started,
                    Math.Max(0, (long)(now - call.Started).TotalMilliseconds)));
            }
        }
        if (e.Final?.PermissionDenials is { } denials)
        {
            foreach (var d in denials)
            {
                if (d.Id.Length > 0 && _finished.Contains(d.Id))
                {
                    // Already finished as an error: if it is still waiting to go out, it was a refusal.
                    var i = _ready.FindIndex(r => r.ToolUseId == d.Id);
                    if (i >= 0 && !_ready[i].Denied) { _ready[i] = _ready[i] with { Ok = false, Denied = true }; }
                    continue;
                }
                var started = d.Id.Length > 0 && _open.Remove(d.Id, out var open) ? open.Started : now;
                Finish(new RunnerToolEvent(d.Id.Length > 0 ? d.Id : $"denied-{_recorded + _overflow + 1}", d.Tool, d.Summary, false, true, started,
                    Math.Max(0, (long)(now - started).TotalMilliseconds)));
            }
        }
    }

    /// <summary>True when a batch should go out now (enough calls, or a while since the last one).</summary>
    internal bool ShouldFlush(DateTimeOffset now) => _ready.Count >= BatchSize || (_ready.Count > 0 && now - _lastFlush >= FlushEvery);

    /// <summary>The finished calls not yet taken, oldest first; resets the flush clock.</summary>
    internal IReadOnlyList<RunnerToolEvent> Take(DateTimeOffset now)
    {
        _lastFlush = now;
        if (_ready.Count == 0) { return []; }
        var batch = _ready.ToArray();
        _ready.Clear();
        return batch;
    }

    /// <summary>The run ended (a result, Stop, a crash): calls still waiting for a result close as errors, an
    /// overflow is reported as one row, and everything left is returned.</summary>
    internal IReadOnlyList<RunnerToolEvent> Complete(DateTimeOffset now)
    {
        foreach (var (id, call) in _open)
        {
            Finish(new RunnerToolEvent(id, call.Tool, call.Summary + (call.Summary.Length > 0 ? " " : "") + "(no result — the run ended first)",
                false, false, call.Started, Math.Max(0, (long)(now - call.Started).TotalMilliseconds)));
        }
        _open.Clear();
        if (_overflow > 0)
        {
            _ready.Add(new RunnerToolEvent($"overflow-{now.ToUnixTimeMilliseconds()}", "(more)",
                $"{_overflow.ToString(System.Globalization.CultureInfo.InvariantCulture)} more tool call(s) were made but not itemised (limit {MaxPerRun} per run)",
                true, false, now, 0));
            _overflow = 0;
        }
        return Take(now);
    }

    private void Finish(RunnerToolEvent call)
    {
        _finished.Add(call.ToolUseId);
        if (_recorded >= MaxPerRun) { _overflow++; return; }
        _recorded++;
        _ready.Add(call);
    }
}
