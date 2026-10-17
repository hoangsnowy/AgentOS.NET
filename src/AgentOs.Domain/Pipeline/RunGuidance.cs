// The repository guidance in force for the current run (E0.7). Set by the caller that knows the target repo
// (the Board's Quality run), read by every agent when it builds its system prompt — an AsyncLocal so it flows
// through the pipeline's async/threadpool hops without widening every agent signature. Null = no guidance.

using System;
using System.Threading;

namespace AgentOs.Domain.Pipeline;

/// <summary>Ambient repository-guidance block for the current async flow.</summary>
public static class RunGuidance
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>The prompt block to append, or null.</summary>
    public static string? Current => _current.Value;

    /// <summary>Set the block for this flow; dispose to restore the previous one. Empty/null clears it.</summary>
    public static IDisposable Push(string? block)
    {
        var previous = _current.Value;
        _current.Value = string.IsNullOrWhiteSpace(block) ? null : block;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) { return; }
            _current.Value = previous;
            _done = true;
        }
    }
}
