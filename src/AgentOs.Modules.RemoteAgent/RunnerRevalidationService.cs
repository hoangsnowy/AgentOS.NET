// GAP-04 backstop. The Web and the Api each host the runner hub with their own in-process broker, so a revoke made on one
// host (or a member removed there, or a row changed in the database) can't abort a connection held by the other. Every
// RevalidateInterval this service re-reads the row of each runner connected HERE and disconnects any that is revoked
// or no longer exists. The in-process revoke paths cut a runner off at once; this bounds the cross-host window.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>Periodically aborts live connections of runners whose row is revoked or gone.</summary>
public sealed partial class RunnerRevalidationService(
    IRunnerDisconnector disconnector, IServiceScopeFactory scopes, ILogger<RunnerRevalidationService> logger, TimeProvider? time = null)
    : BackgroundService
{
    /// <summary>How often live runners are re-checked against their row.</summary>
    public static readonly TimeSpan RevalidateInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RevalidateInterval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RevalidateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A database hiccup must not stop the loop; the next tick tries again.
                LogFailed(logger, ex);
            }
        }
    }

    /// <summary>One pass: disconnect every connected runner whose row is revoked or missing. Returns how many.</summary>
    public async Task<int> RevalidateAsync(CancellationToken ct = default)
    {
        var connected = disconnector.ConnectedRunners;
        if (connected.Count == 0) { return 0; }
        var cut = 0;
        await using var scope = scopes.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IRunnerDirectory>();
        foreach (var runnerId in connected)
        {
            var row = await directory.FindForPairingAsync(runnerId, ct).ConfigureAwait(false);
            if (row is null || string.Equals(row.Status, "Revoked", StringComparison.Ordinal))
            {
                cut += disconnector.Disconnect(runnerId) > 0 ? 1 : 0;
                LogRevoked(logger, runnerId);
            }
        }
        return cut;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Runner {RunnerId} is revoked or gone — its live connection was aborted.")]
    private static partial void LogRevoked(ILogger logger, Guid runnerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runner revalidation pass failed; retrying on the next tick.")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
