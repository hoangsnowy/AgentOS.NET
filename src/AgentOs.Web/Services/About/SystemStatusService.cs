// Runs every IServiceProbe in parallel and shares the result. Probing costs real round-trips (a Postgres
// connection, an OIDC discovery fetch, a Keycloak admin login, an SMTP handshake), so:
//   • one snapshot is cached for Ttl and shared by every viewer (it holds no tenant data);
//   • concurrent callers share ONE in-flight run (single-flight) — ten open About pages never cost ten probes;
//   • each probe gets its own timeout, so one dead service can't stall the page; it shows as Down instead.
// A caller's cancellation only stops that caller waiting — never the shared run other viewers depend on.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AgentOs.Web.Services.About;

/// <summary>Live status of one service the Web depends on. Implementations are singletons and must be
/// cheap to construct; <see cref="ProbeAsync"/> may throw — the aggregator turns a throw into Down.</summary>
public interface IServiceProbe
{
    /// <summary>Position on the About page (ascending).</summary>
    int Order { get; }

    /// <summary>The card shown when the probe throws or times out.</summary>
    ServiceStatus Unreachable(string reason);

    /// <summary>Probe the service.</summary>
    Task<ServiceStatus> ProbeAsync(CancellationToken ct);
}

/// <summary>Aggregates <see cref="IServiceProbe"/>s into a cached, single-flight <see cref="SystemSnapshot"/>.</summary>
public sealed class SystemStatusService
{
    /// <summary>How long a snapshot is reused before the next viewer triggers a fresh probe.</summary>
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    /// <summary>Per-probe budget. A probe that exceeds it is reported as Down ("timed out").</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private readonly IServiceProbe[] _probes;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeProvider _time;
    private readonly ILogger<SystemStatusService> _logger;
    private readonly Lock _gate = new();
    private SystemSnapshot? _last;
    private Task<SystemSnapshot>? _inflight;

    public SystemStatusService(IEnumerable<IServiceProbe> probes, TimeProvider time, ILogger<SystemStatusService> logger)
        : this(probes, time, logger, ProbeTimeout)
    {
    }

    /// <summary>Test seam: a shorter per-probe budget.</summary>
    internal SystemStatusService(IEnumerable<IServiceProbe> probes, TimeProvider time, ILogger<SystemStatusService> logger, TimeSpan probeTimeout)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = [.. probes.OrderBy(p => p.Order)];
        _probeTimeout = probeTimeout;
        _time = time;
        _logger = logger;
    }

    /// <summary>The most recent snapshot, if any — lets a page paint immediately while a refresh runs.</summary>
    public SystemSnapshot? Last
    {
        get { lock (_gate) { return _last; } }
    }

    /// <summary>The cached snapshot while it is fresh, else the result of a (shared) probe run.
    /// <paramref name="refresh"/> skips the cache but still joins a run already in flight.</summary>
    public Task<SystemSnapshot> GetAsync(bool refresh = false, CancellationToken ct = default)
    {
        Task<SystemSnapshot> run;
        lock (_gate)
        {
            if (!refresh && _last is { } fresh && _time.GetUtcNow() - fresh.CheckedAt < Ttl)
            {
                return Task.FromResult(fresh);
            }
            // A completed task is replaced, never cleared from inside the run — a run that finishes
            // synchronously would otherwise clear the field before this assignment and stick forever.
            if (_inflight is not { IsCompleted: false })
            {
                _inflight = RunAsync();
            }
            run = _inflight;
        }
        return run.WaitAsync(ct);
    }

    private async Task<SystemSnapshot> RunAsync()
    {
        var results = await Task.WhenAll(_probes.Select(ProbeOneAsync)).ConfigureAwait(false);
        var snapshot = new SystemSnapshot(_time.GetUtcNow(), results);
        lock (_gate) { _last = snapshot; }
        return snapshot;
    }

    private async Task<ServiceStatus> ProbeOneAsync(IServiceProbe probe)
    {
        using var cts = new CancellationTokenSource(_probeTimeout, _time);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await probe.ProbeAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return probe.Unreachable($"Timed out after {_probeTimeout.TotalSeconds:0.#} s");
        }
#pragma warning disable CA1031 // A probe failure of ANY kind is a status to show, never a page crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "About probe {Probe} failed after {Ms} ms", probe.GetType().Name,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return probe.Unreachable(ex.Message);
        }
    }
}
