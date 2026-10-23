// A keyed gate whose capacity is read at entry: at most N holders per key at once, N taken from the caller each time
// (the tenant's Settings › Autopilot › Acceptance tests at once, so a change applies to the next run without a
// restart). Keyed by the computer's owner — two members' computers are different machines, so they never wait on
// each other. A release wakes every waiter to re-check; holders are few (single digits), so that is cheap.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Web.Services.BoardTasks;

/// <summary>At most N concurrent holders per key. Thread-safe.</summary>
public sealed class RunGate
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _held = new(StringComparer.Ordinal);
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Holders of <paramref name="key"/> right now.</summary>
    public int Held(string key)
    {
        lock (_lock) { return _held.GetValueOrDefault(key); }
    }

    /// <summary>Wait until <paramref name="key"/> has fewer than <paramref name="capacity"/> holders, then hold it;
    /// dispose the result to let go.</summary>
    public async Task<IDisposable> EnterAsync(string key, int capacity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var cap = Math.Max(1, capacity);
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                var held = _held.GetValueOrDefault(key);
                if (held < cap)
                {
                    _held[key] = held + 1;
                    return new Lease(this, key);
                }
                wait = _released.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void Exit(string key)
    {
        TaskCompletionSource wake;
        lock (_lock)
        {
            var held = _held.GetValueOrDefault(key) - 1;
            if (held <= 0) { _held.Remove(key); } else { _held[key] = held; }
            wake = _released;
            _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        wake.TrySetResult();
    }

    private sealed class Lease(RunGate gate, string key) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) { gate.Exit(key); } }
    }
}
