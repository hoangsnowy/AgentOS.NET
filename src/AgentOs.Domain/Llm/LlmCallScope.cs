// Metrics first: every LLM call an agent makes inside a scope is recorded — tokens (fresh / cache read / cache write /
// output / reasoning), cost, latency, provider, outcome. The Board's agents (Coding, Reviewer, Testing, Discovery, the
// Autopilot Orchestrator) run outside the 5-agent pipeline, so their calls never reached Insights; the caller opens a
// scope around one agent run and stores what it collected as one run with its calls.

using System;
using System.Collections.Generic;
using System.Threading;

namespace AgentOs.Domain.Llm;

/// <summary>One LLM call as recorded for metrics.</summary>
public sealed record LlmCallRecord(
    string? AgentRole,
    string Model,
    string Provider,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    int ReasoningTokens,
    decimal CostUsd,
    TimeSpan Latency,
    bool Success,
    string? Error,
    DateTimeOffset At);

/// <summary>Collects the LLM calls made in the current async flow while it is open (innermost scope wins).</summary>
public sealed class LlmCallScope : IDisposable
{
    private static readonly AsyncLocal<LlmCallScope?> CurrentScope = new();

    private readonly LlmCallScope? _previous;
    private readonly List<LlmCallRecord> _calls = [];
    private bool _disposed;

    private LlmCallScope(LlmCallScope? previous) => _previous = previous;

    /// <summary>The open scope of this flow, or null.</summary>
    public static LlmCallScope? Current => CurrentScope.Value;

    /// <summary>Open a scope for this async flow; dispose to close it.</summary>
    public static LlmCallScope Begin()
    {
        var scope = new LlmCallScope(CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>The calls recorded so far.</summary>
    public IReadOnlyList<LlmCallRecord> Calls
    {
        get { lock (_calls) { return [.. _calls]; } }
    }

    /// <summary>Record one call.</summary>
    public void Add(LlmCallRecord call)
    {
        ArgumentNullException.ThrowIfNull(call);
        lock (_calls) { _calls.Add(call); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (ReferenceEquals(CurrentScope.Value, this)) { CurrentScope.Value = _previous; }
    }
}
