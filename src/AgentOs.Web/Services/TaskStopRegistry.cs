// "Stop" on a running Board task. A task runs fire-and-forget off the circuit (the member can close the tab), so
// its cancellation lives in a process-wide registry keyed by (tenant, session) — a reloaded page, or another
// member of the same tenant with access to the row, can still stop it. Cancelling the token cancels the agent's
// LLM dispatch; for a paired computer the broker then tells the runner to kill the CLI's process tree.

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace AgentOs.Web.Services;

/// <summary>Cancellation for running Board tasks. Singleton; thread-safe.</summary>
public sealed class TaskStopRegistry
{
    private readonly ConcurrentDictionary<(string Tenant, Guid Session), CancellationTokenSource> _running = new();

    /// <summary>Start tracking a run; dispose the lease when the run ends. A second run of the same task replaces
    /// (and cancels) the first, so only one run per task is ever live.</summary>
    public Lease Begin(string tenantId, Guid sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var cts = new CancellationTokenSource();
        var key = (tenantId, sessionId);
        _running.AddOrUpdate(key, cts, (_, previous) =>
        {
            previous.Cancel();
            return cts;
        });
        return new Lease(this, key, cts);
    }

    /// <summary>Ask a running task to stop. False when it isn't running (already finished, or another process).</summary>
    public bool Stop(string tenantId, Guid sessionId)
    {
        if (!_running.TryGetValue((tenantId, sessionId), out var cts)) { return false; }
        cts.Cancel();
        return true;
    }

    /// <summary>Whether a run of this task is live in this process.</summary>
    public bool IsRunning(string tenantId, Guid sessionId) => _running.ContainsKey((tenantId, sessionId));

    private void End((string, Guid) key, CancellationTokenSource cts)
    {
        // Only remove our own entry — a newer run of the same task may have replaced it.
        _running.TryRemove(new System.Collections.Generic.KeyValuePair<(string, Guid), CancellationTokenSource>(key, cts));
        cts.Dispose();
    }

    /// <summary>One tracked run.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly TaskStopRegistry _owner;
        private readonly (string, Guid) _key;
        private readonly CancellationTokenSource _cts;
        private int _disposed;

        internal Lease(TaskStopRegistry owner, (string, Guid) key, CancellationTokenSource cts)
        {
            _owner = owner;
            _key = key;
            _cts = cts;
            Token = cts.Token;
        }

        /// <summary>Cancelled when someone presses Stop (or a newer run replaces this one).</summary>
        public CancellationToken Token { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) { _owner.End(_key, _cts); }
        }
    }
}
