// Live count of open desktop sessions (Blazor circuits) on this Web instance, for System → About.
// One Interlocked counter (singleton) fed by a per-circuit CircuitHandler — O(1), no list of sessions kept.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace AgentOs.Web.Services.About;

/// <summary>Number of circuits currently open on this instance.</summary>
public sealed class CircuitCounter
{
    private int _open;

    /// <summary>Open circuits right now.</summary>
    public int Open => Volatile.Read(ref _open);

    internal void Opened() => Interlocked.Increment(ref _open);

    internal void Closed() => Interlocked.Decrement(ref _open);
}

/// <summary>Feeds <see cref="CircuitCounter"/>. Registered scoped (one per circuit), as Blazor requires.</summary>
internal sealed class CircuitCounterHandler(CircuitCounter counter) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        counter.Opened();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        counter.Closed();
        return Task.CompletedTask;
    }
}
