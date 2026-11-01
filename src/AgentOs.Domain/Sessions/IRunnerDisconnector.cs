// GAP-04 (docs/security/threat-model.md §15) — revoking a runner must end its access NOW, not at its next reconnect.
// A revoked row only stops the NEXT handshake; a runner already connected kept receiving work and could keep answering.
// IRunnerDisconnector is the seam the transport implements (the in-process broker drops the connection from routing
// and aborts the SignalR connection); every revoke path calls it after the row is marked Revoked. RunnerRevocation
// bundles the two steps for "a member was removed": revoke all of the member's runners, then drop the live ones.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Sessions;

/// <summary>Ends the live connections of a runner (implemented by the runner transport).</summary>
public interface IRunnerDisconnector
{
    /// <summary>The runners with at least one live connection on this server instance.</summary>
    IReadOnlyCollection<Guid> ConnectedRunners { get; }

    /// <summary>Stop routing work to <paramref name="runnerId"/> and abort every live connection it has here. Returns how
    /// many connections were aborted (0 when it is not connected to this instance).</summary>
    int Disconnect(Guid runnerId);
}

/// <summary>Revoke-and-disconnect helpers shared by the Api and the desktop.</summary>
public static class RunnerRevocation
{
    /// <summary>Revoke every runner <paramref name="userId"/> owns in <paramref name="tenantId"/> and abort their live
    /// connections. Returns the revoked runner ids.</summary>
    public static async Task<IReadOnlyList<Guid>> RevokeMemberAsync(
        IRunnerDirectory directory, IRunnerDisconnector? disconnector, string tenantId, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var revoked = await directory.RevokeForMemberAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (disconnector is not null)
        {
            foreach (var id in revoked) { disconnector.Disconnect(id); }
        }
        return revoked;
    }
}
