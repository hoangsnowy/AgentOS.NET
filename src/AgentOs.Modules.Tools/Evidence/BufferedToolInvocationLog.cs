// M6 perf — takes evidence writes OFF the tool-call critical path. The gateway awaits
// IToolInvocationLog.AppendAsync after every governed tool call (DefaultToolGateway), and the durable
// EF sink does a DI-scope + SaveChanges per call — so a session run's agentic loop paid N serial DB
// round-trips on its wall-time. This decorator makes AppendAsync a non-blocking enqueue and drains the
// queue to the inner sink on one background loop.
//
// Enterprise review E0.3 — evidence is the governance trail, so it must NEVER be shed silently:
//   • queue full (a flood) → the entry is persisted INLINE instead of evicting an older one: slower under
//     flood, never lossy;
//   • the inner sink faults → retried with backoff (3 attempts);
//   • only when every attempt failed is the entry lost — and then it is counted (`agentos.evidence.lost`),
//     logged as an error naming the tenant + tool, and exposed via LostCount for the About page and alerts.
// Pure BCL channels (same pattern as InMemorySessionRunFeed) — no hosting dependency.

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;
using AgentOs.SharedKernel.Logging;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Tools.Evidence;

/// <summary>Non-blocking, lossless-under-flood buffering decorator over an inner <see cref="IToolInvocationLog"/>.</summary>
internal sealed partial class BufferedToolInvocationLog : IToolInvocationLog, IAsyncDisposable
{
    /// <summary>Meter for evidence health (exported with the host's OTel metrics).</summary>
    public const string MeterName = ToolTelemetry.MeterName;

    internal const int Capacity = 4096;

    private static readonly Meter Meter = ToolTelemetry.Meter;
    private static readonly Counter<long> InlineWrites = Meter.CreateCounter<long>("agentos.evidence.inline_writes", description: "Evidence rows persisted inline because the queue was full.");
    private static readonly Counter<long> Lost = Meter.CreateCounter<long>("agentos.evidence.lost", description: "Evidence rows lost after every persistence attempt failed.");

    private readonly IToolInvocationLog _inner;
    private readonly ILogger? _logger;
    private readonly Channel<ToolInvocationEvidence> _channel;
    private readonly TimeSpan _retryDelay;
    private CancellationTokenSource? _cts;
    private Task? _drainLoop;
    private long _lost;
    private long _inline;

    public BufferedToolInvocationLog(IToolInvocationLog inner, ILogger<BufferedToolInvocationLog>? logger = null)
        : this(inner, logger, capacity: Capacity, retryDelay: TimeSpan.FromMilliseconds(100))
    {
    }

    /// <summary>Test seam: a small capacity and no retry wait.</summary>
    internal BufferedToolInvocationLog(IToolInvocationLog inner, ILogger<BufferedToolInvocationLog>? logger, int capacity, TimeSpan retryDelay)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _logger = logger;
        _retryDelay = retryDelay;
        // Bounded + Wait: TryWrite fails fast when full (never blocks the publisher, never grows memory), and the
        // caller then persists inline — nothing already queued is ever evicted.
        _channel = Channel.CreateBounded<ToolInvocationEvidence>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Entries lost after every attempt failed (should stay 0; alert when it isn't).</summary>
    public long LostCount => Interlocked.Read(ref _lost);

    /// <summary>Entries persisted inline because the queue was full.</summary>
    public long InlineCount => Interlocked.Read(ref _inline);

    /// <summary>Enqueue + return immediately; under flood, persist inline instead of dropping. Never throws.</summary>
    public Task AppendAsync(ToolInvocationEvidence entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_channel.Writer.TryWrite(entry))
        {
            return Task.CompletedTask;
        }
        Interlocked.Increment(ref _inline);
        InlineWrites.Add(1);
        return PersistAsync(entry);
    }

    /// <summary>Reads delegate to the inner sink (already-persisted rows; queued rows appear once drained).</summary>
    public Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(
        string tenantId, int limit = 50, CancellationToken cancellationToken = default) =>
        _inner.ListRecentAsync(tenantId, limit, cancellationToken);

    /// <summary>Start the single background drain loop. Called once at module init (NOT in tests, which
    /// drive <see cref="DrainPendingAsync"/> as the sole reader for determinism).</summary>
    public void StartDraining()
    {
        if (_drainLoop is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _drainLoop = Task.Run(() => DrainLoopAsync(_cts.Token));
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // PersistAsync never throws, so one bad row can't kill the only drain loop.
                await PersistAsync(entry).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    // Up to three attempts with backoff. Not cancelled: a write already accepted must finish persisting.
    private async Task PersistAsync(ToolInvocationEvidence entry)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _inner.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < attempts)
            {
                if (_logger is not null) { LogRetry(_logger, ex, attempt); }
                if (_retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_retryDelay * attempt * attempt).ConfigureAwait(false);
                }
            }
            // The last failure is counted and logged — evidence persistence must never throw into a tool call.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref _lost);
                Lost.Add(1);
                if (_logger is not null) { LogLost(_logger, ex, LogSafe.Scrub(entry.TenantId), LogSafe.Scrub(entry.ToolName)); }
                return;
            }
        }
    }

    /// <summary>Drain everything currently queued into the inner sink. Test-only: safe only when the background
    /// loop is NOT running (otherwise two readers race the single-reader channel).</summary>
    internal async Task DrainPendingAsync()
    {
        while (_channel.Reader.TryRead(out var entry))
        {
            await PersistAsync(entry).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Complete the writer so the drain loop persists what's left, then await it (bounded).
        _channel.Writer.TryComplete();
        if (_drainLoop is not null)
        {
            try { await _drainLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            // Best-effort flush on shutdown — a timeout or a final persistence failure must not block dispose.
            catch (TimeoutException ex) { _ = ex.Message; }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) { _ = ex.Message; }
            catch (System.Data.Common.DbException ex) { _ = ex.Message; }
            catch (ObjectDisposedException ex) { _ = ex.Message; }
            catch (InvalidOperationException ex) { _ = ex.Message; }
        }
        _cts?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evidence write failed (attempt {Attempt}); retrying.")]
    private static partial void LogRetry(ILogger logger, Exception ex, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Evidence row LOST after every attempt failed — tenant {TenantId}, tool {Tool}. The governance trail has a gap; check the evidence store.")]
    private static partial void LogLost(ILogger logger, Exception ex, string tenantId, string tool);
}
