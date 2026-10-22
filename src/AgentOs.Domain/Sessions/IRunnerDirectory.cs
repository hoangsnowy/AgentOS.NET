// M3 — read-only lookup the SignalR hub uses to authenticate a connecting runner. The connect handshake
// carries no tenant context (the token IS the credential), so this lookup is deliberately tenant-
// unfiltered: it finds the runner row by its unguessable id, and the caller then verifies the presented
// token against the returned hash before trusting TenantId / OwnerUserId for routing.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Sessions;

/// <summary>The pairing-relevant projection of a runner row.</summary>
public sealed record RunnerIdentity(Guid RunnerId, string TenantId, string OwnerUserId, string TokenHash, string Status);

/// <summary>Resolves a runner by id for the pairing handshake (no tenant filter — see file header).</summary>
public interface IRunnerDirectory
{
    /// <summary>Find a runner by id for pairing, or <c>null</c> if it does not exist.</summary>
    Task<RunnerIdentity?> FindForPairingAsync(Guid runnerId, CancellationToken cancellationToken = default);

    /// <summary>Record that a runner completed the handshake: <c>Pending → Paired</c> plus a fresh
    /// last-seen stamp. Called only after the presented token has been verified. A revoked runner is
    /// left untouched. Returns <c>true</c> when a row was updated.</summary>
    Task<bool> MarkPairedAsync(Guid runnerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same computer connected again under a new pairing: retire (revoke) the member's OLDER entries with the same
    /// name that are not connected right now, so Board › Computers shows one row per computer and the old tokens stop
    /// working. Called only after the new runner's token was verified. Returns how many were retired.
    /// </summary>
    Task<int> RetireReplacedAsync(Guid runnerId, Func<Guid, bool> isConnected, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
