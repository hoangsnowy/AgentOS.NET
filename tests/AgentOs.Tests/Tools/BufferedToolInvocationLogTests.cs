// M6 perf — the buffered evidence log must take writes OFF the caller's path (enqueue + return), drain
// to the inner sink (background loop or explicit drain), delegate reads, and never throw.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;
using AgentOs.Modules.Tools.Evidence;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tools;

public sealed class BufferedToolInvocationLogTests
{
    // Thread-safe recorder (the background drain loop appends off-thread while the test reads).
    private sealed class FakeInnerLog : IToolInvocationLog
    {
        public ConcurrentQueue<ToolInvocationEvidence> Appended { get; } = new();

        public Task AppendAsync(ToolInvocationEvidence entry, CancellationToken cancellationToken = default)
        {
            Appended.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(
            string tenantId, int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolInvocationEvidence>>(Appended.ToArray());
    }

    // Inner sink that throws on one specific entry, succeeds otherwise — models a transient DB fault.
    private sealed class FlakyInnerLog : IToolInvocationLog
    {
        private readonly string _failCallId;
        public ConcurrentQueue<ToolInvocationEvidence> Appended { get; } = new();
        public FlakyInnerLog(string failCallId) => _failCallId = failCallId;

        public Task AppendAsync(ToolInvocationEvidence entry, CancellationToken cancellationToken = default)
        {
            if (string.Equals(entry.CallId, _failCallId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("sink boom");
            }
            Appended.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(
            string tenantId, int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolInvocationEvidence>>(Appended.ToArray());
    }

    private static ToolInvocationEvidence Evt(string callId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ToolInvocationEvidence(callId, "runner_shell", "t1", null, "{}", "ok", false, now, now, "sess-1");
    }

    [Fact]
    public async Task AppendAsync_IsNonBlocking_AndDoesNotWriteToInnerSynchronously()
    {
        var inner = new FakeInnerLog();
        await using var buffered = new BufferedToolInvocationLog(inner);

        var task = buffered.AppendAsync(Evt("c1"));
        task.IsCompletedSuccessfully.ShouldBeTrue();   // returns synchronously — off the critical path
        await task;
        await buffered.AppendAsync(Evt("c2"));

        // Nothing started a drain → the inner sink hasn't been touched yet.
        inner.Appended.Count.ShouldBe(0);
    }

    [Fact]
    public async Task DrainPendingAsync_FlushesQueuedEntriesToInner()
    {
        var inner = new FakeInnerLog();
        await using var buffered = new BufferedToolInvocationLog(inner);

        await buffered.AppendAsync(Evt("c1"));
        await buffered.AppendAsync(Evt("c2"));
        await buffered.DrainPendingAsync();

        inner.Appended.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ListRecentAsync_DelegatesToInner()
    {
        var inner = new FakeInnerLog();
        await using var buffered = new BufferedToolInvocationLog(inner);
        await buffered.AppendAsync(Evt("c1"));
        await buffered.DrainPendingAsync();

        var rows = await buffered.ListRecentAsync("t1");
        rows.Count.ShouldBe(1);
        rows[0].CallId.ShouldBe("c1");
    }

    [Fact]
    public async Task StartDraining_PersistsEnqueuedEntries_InBackground()
    {
        var inner = new FakeInnerLog();
        await using var buffered = new BufferedToolInvocationLog(inner);
        buffered.StartDraining();

        await buffered.AppendAsync(Evt("c1"));
        await buffered.AppendAsync(Evt("c2"));

        // The background loop drains asynchronously — poll until both land (bounded).
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (inner.Appended.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        inner.Appended.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DrainLoop_SurvivesInnerSinkThrow_AndKeepsPersistingLaterEntries()
    {
        // Regression: a single failed inner write must NOT kill the only drain loop — otherwise ALL
        // subsequent evidence is silently lost for the process lifetime (governance audit-trail hole).
        var inner = new FlakyInnerLog(failCallId: "bad");
        await using var buffered = new BufferedToolInvocationLog(inner);
        buffered.StartDraining();

        await buffered.AppendAsync(Evt("bad"));    // throws inside the loop
        await buffered.AppendAsync(Evt("good"));   // must still persist — the loop must have survived

        var deadline = DateTime.UtcNow.AddSeconds(2);
        var goodPersisted = false;
        while (!goodPersisted && DateTime.UtcNow < deadline)
        {
            goodPersisted = inner.Appended.Any(e => string.Equals(e.CallId, "good", StringComparison.Ordinal));
            if (!goodPersisted) { await Task.Delay(20); }
        }

        goodPersisted.ShouldBeTrue();
    }

    // E0.3 — a flood must never evict queued evidence: past capacity, entries are persisted inline.
    [Fact]
    public async Task Flood_PastCapacity_PersistsInline_LosesNothing()
    {
        var inner = new FakeInnerLog();
        await using var buffered = new BufferedToolInvocationLog(inner, null, capacity: 4, retryDelay: TimeSpan.Zero);

        for (var i = 0; i < 10; i++)
        {
            await buffered.AppendAsync(Evt($"c{i}"));
        }
        inner.Appended.Count.ShouldBe(6);            // 4 queued, 6 written inline the moment the queue was full
        buffered.InlineCount.ShouldBe(6);
        await buffered.DrainPendingAsync();

        inner.Appended.Select(e => e.CallId).OrderBy(c => c, StringComparer.Ordinal)
            .ShouldBe(Enumerable.Range(0, 10).Select(i => $"c{i}").OrderBy(c => c, StringComparer.Ordinal));
        buffered.LostCount.ShouldBe(0);
    }

    // E0.3 — a transient sink fault is retried, so the row still lands.
    [Fact]
    public async Task TransientSinkFault_IsRetried_AndTheRowLands()
    {
        var inner = new FailNTimesInnerLog(failures: 2);
        await using var buffered = new BufferedToolInvocationLog(inner, null, capacity: 16, retryDelay: TimeSpan.Zero);
        await buffered.AppendAsync(Evt("c1"));
        await buffered.DrainPendingAsync();

        inner.Appended.ShouldHaveSingleItem().CallId.ShouldBe("c1");
        buffered.LostCount.ShouldBe(0);
    }

    // E0.3 — only when every attempt failed is a row lost, and then it is counted (never silent).
    [Fact]
    public async Task PermanentSinkFault_IsCountedAsLost_NeverThrows()
    {
        var inner = new FlakyInnerLog(failCallId: "bad");
        await using var buffered = new BufferedToolInvocationLog(inner, null, capacity: 16, retryDelay: TimeSpan.Zero);
        await buffered.AppendAsync(Evt("bad"));
        await Should.NotThrowAsync(() => buffered.DrainPendingAsync());
        buffered.LostCount.ShouldBe(1);
    }

    private sealed class FailNTimesInnerLog(int failures) : IToolInvocationLog
    {
        private int _left = failures;
        public ConcurrentQueue<ToolInvocationEvidence> Appended { get; } = new();

        public Task AppendAsync(ToolInvocationEvidence entry, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _left) >= 0) { throw new InvalidOperationException("transient"); }
            Appended.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(string tenantId, int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ToolInvocationEvidence>>(Appended.ToArray());
    }
}
