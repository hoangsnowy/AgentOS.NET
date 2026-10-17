// Enterprise review E0.6 — per-tenant admission for agent runs. The desktop runs the engine IN-PROCESS (no HTTP
// hop through the Api's rate limiter), so the limit lives where every run starts: the pipeline orchestrator,
// the Workflow executor and the Quick engine ask for a lease before any agent runs. Bounds one tenant's burst
// (runs per minute) and its parallel load (concurrent runs) so it can't starve the others. Complements the
// month-to-date budget gate, which is post-hoc and cannot bound a burst.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Pipeline;

/// <summary>Grants or refuses a run for a tenant.</summary>
public interface IRunAdmission
{
    /// <summary>A lease when admitted (dispose it when the run ends), or a refusal with the reason.</summary>
    ValueTask<RunAdmissionLease> AdmitAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of <see cref="IRunAdmission.AdmitAsync"/>. Disposing an admitted lease frees its slot.</summary>
public sealed class RunAdmissionLease : IDisposable
{
    private Action? _release;

    private RunAdmissionLease(bool admitted, string? reason, Action? release)
    {
        Admitted = admitted;
        Reason = reason;
        _release = release;
    }

    /// <summary>True when the run may start.</summary>
    public bool Admitted { get; }

    /// <summary>Why the run was refused (null when admitted).</summary>
    public string? Reason { get; }

    /// <summary>An admitted lease; <paramref name="release"/> runs once on dispose.</summary>
    public static RunAdmissionLease Grant(Action? release = null) => new(true, null, release);

    /// <summary>A refusal.</summary>
    public static RunAdmissionLease Deny(string reason) => new(false, reason, null);

    /// <summary>Throws <see cref="RunAdmissionDeniedException"/> when refused.</summary>
    public RunAdmissionLease EnsureAdmitted() => Admitted ? this : throw new RunAdmissionDeniedException(Reason!);

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>A run refused by per-tenant admission (too many concurrent runs, or too many per minute).</summary>
public sealed class RunAdmissionDeniedException : InvalidOperationException
{
    public RunAdmissionDeniedException(string message) : base(message) { }

    public RunAdmissionDeniedException() { }

    public RunAdmissionDeniedException(string message, Exception innerException) : base(message, innerException) { }
}
