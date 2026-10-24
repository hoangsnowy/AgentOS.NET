// A Board agent that reuses a Workflow-pipeline specialist (New idea's Discovery runs the Requirement agent) opens a
// scope around that call: the specialist then resolves the Board role's profile instead of its own, files its call
// under the Board role (Insights groups by it), and passes the budget gate the Board agents pass. Its prompt and output
// contract stay the specialist's own. AsyncLocal: the scope covers exactly the awaited call chain that opened it.

using System;
using System.Threading;
using AgentOs.Domain.Cost;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Runs the specialist agents of this async flow as <see cref="Role"/>.</summary>
public sealed class AgentRunScope : IDisposable
{
    private static readonly AsyncLocal<AgentRunScope?> CurrentScope = new();

    private readonly AgentRunScope? _previous;
    private bool _disposed;

    private AgentRunScope(string role, AgentOptions? fallback, IBudgetGuard? budget, AgentRunScope? previous)
    {
        Role = role;
        Fallback = fallback;
        Budget = budget;
        _previous = previous;
    }

    /// <summary>The open scope of this flow, or null.</summary>
    public static AgentRunScope? Current => CurrentScope.Value;

    /// <summary>The role key (an <see cref="AgentRoles"/> key) whose profile and metrics name apply.</summary>
    public string Role { get; }

    /// <summary>The role's seed profile when no settings registry is wired; null = the specialist's own.</summary>
    public AgentOptions? Fallback { get; }

    /// <summary>The budget gate the calls pass (null = none).</summary>
    public IBudgetGuard? Budget { get; }

    /// <summary>Open a scope for this async flow; dispose to close it.</summary>
    public static AgentRunScope Begin(string role, AgentOptions? fallback = null, IBudgetGuard? budget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var scope = new AgentRunScope(role, fallback, budget, CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        CurrentScope.Value = _previous;
    }
}
