// Fire-and-forget queues on the agent's critical path: the live session-run feed (published on every run step, fanned
// out to every open desktop of the tenant) and the buffered tool-evidence log (awaited after every governed tool call).
// §2.7 budget for an enqueue: < 2 µs.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Tools;
using AgentOs.Modules.Tools.Evidence;
using BenchmarkDotNet.Attributes;

namespace AgentOs.Benchmarks.Feeds;

/// <summary><see cref="InMemorySessionRunFeed"/> publish fan-out and a subscribe round trip.</summary>
[MemoryDiagnoser]
public class SessionRunFeedBenchmarks : IAsyncDisposable
{
    private InMemorySessionRunFeed _feed = null!;
    private CancellationTokenSource _cts = null!;
    private readonly List<IAsyncEnumerator<SessionRunEvent>> _subscribers = [];
    private readonly List<Task<bool>> _pending = [];
    private SessionRunEvent _evt = null!;
    private SessionRunEvent _otherTenantEvt = null!;

    /// <summary>Subscribers open on the desktop — one per open window of the tenant.</summary>
    [Params(0, 4)]
    public int Subscribers { get; set; }

    /// <summary>Open <see cref="Subscribers"/> subscriptions (registered on their first MoveNextAsync).</summary>
    [GlobalSetup]
    public void Setup()
    {
        _feed = new InMemorySessionRunFeed();
        _cts = new CancellationTokenSource();
        for (var i = 0; i < Subscribers; i++)
        {
            var e = _feed.Subscribe("acme", _cts.Token).GetAsyncEnumerator(_cts.Token);
            _pending.Add(e.MoveNextAsync().AsTask());   // registers the channel; completes on the first publish
            _subscribers.Add(e);
        }
        _evt = new SessionRunEvent("acme", Guid.NewGuid(), SessionRunEventKind.Step, "Building the solution (dotnet build)…",
            DateTimeOffset.UnixEpoch, Repo: "acme/xiangqi", Status: "Running");
        _otherTenantEvt = _evt with { TenantId = "globex" };
    }

    /// <summary>Cancel the subscriptions.</summary>
    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        if (_cts is null) { return; }
        await _cts.CancelAsync();
        // An async iterator cannot be disposed while a MoveNextAsync is in flight: let each pending read observe the
        // cancellation (or the last published event) first.
        foreach (var pending in _pending)
        {
            try { await pending; }
            catch (OperationCanceledException) { /* expected on cancel */ }
        }
        foreach (var e in _subscribers)
        {
            await e.DisposeAsync();
        }
        _cts.Dispose();
        _cts = null!;
        GC.SuppressFinalize(this);
    }

    /// <summary>Publish to the tenant (bounded DropOldest channels: never blocks, even when nobody reads).</summary>
    [Benchmark(Baseline = true)]
    public void Publish() => _feed.Publish(_evt);

    /// <summary>Publish for a tenant with no open desktop — the common case for background runs.</summary>
    [Benchmark]
    public void PublishNoListeners() => _feed.Publish(_otherTenantEvt);

    /// <summary>A desktop window opening the feed, receiving one event and closing it.</summary>
    [Benchmark]
    public async Task<SessionRunEvent> SubscribeReceiveDispose()
    {
        using var cts = new CancellationTokenSource();
        var e = _feed.Subscribe("initech", cts.Token).GetAsyncEnumerator(cts.Token);
        var next = e.MoveNextAsync();
        _feed.Publish(_evt with { TenantId = "initech" });
        await next;
        var received = e.Current;
        await e.DisposeAsync();
        return received;
    }
}

/// <summary><see cref="BufferedToolInvocationLog.AppendAsync"/> — the evidence write after every governed tool call.</summary>
[MemoryDiagnoser]
public class ToolEvidenceBenchmarks : IAsyncDisposable
{
    private const int Batch = 1_024;
    private BufferedToolInvocationLog _log = null!;
    private ToolInvocationEvidence _entry = null!;

    /// <summary>A no-op inner sink: this measures the queue, not the database.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _log = new BufferedToolInvocationLog(new NullSink(), logger: null);
        _entry = new ToolInvocationEvidence(
            "call_01J9", "build_verifier", "acme", "run-42", "{\"project\":\"src/Xiangqi.Core\"}", "Build succeeded. 0 warnings.",
            IsError: false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMilliseconds(5_400), SessionId: "s-7");
    }

    /// <summary>Dispose the log.</summary>
    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        await _log.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Enqueue a batch, then drain it (per-entry cost = enqueue + dequeue + the inner call).</summary>
    [Benchmark(OperationsPerInvoke = Batch)]
    public async Task EnqueueAndDrain()
    {
        for (var i = 0; i < Batch; i++)
        {
            await _log.AppendAsync(_entry);
        }
        await _log.DrainPendingAsync();
    }

    private sealed class NullSink : IToolInvocationLog
    {
        public Task AppendAsync(ToolInvocationEvidence entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(string tenantId, int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ToolInvocationEvidence>>([]);
    }
}
