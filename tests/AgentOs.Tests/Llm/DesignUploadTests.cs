// Design gate (ADR-0016) — screenshots sent back from a member's computer: the path and content rules both sides share;
// the runner picks only image files inside its run folder (never through a link, never "../" or an absolute path, never
// a renamed text file, within the caps) and sends them in pieces; the broker hears only the connection the run went to
// and re-checks every piece (path, order, sizes, count, the image signature); RemoteAgentLlmClient stores what arrived
// in the tenant's file store under the run's design scope; and the wire contract survives SignalR's JSON protocol.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Domain.Llm;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.RemoteAgent;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class DesignUploadTests : IDisposable
{
    private static readonly byte[] PngHead = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private readonly string _root = Path.Join(Path.GetTempPath(), "agentos-uploads-" + Guid.NewGuid().ToString("N"));
    private readonly string _runDir;

    public DesignUploadTests()
    {
        _runDir = Path.Join(_root, "run");
        Directory.CreateDirectory(_runDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Png(int size)
    {
        var bytes = new byte[Math.Max(size, PngHead.Length)];
        PngHead.CopyTo(bytes, 0);
        return bytes;
    }

    // ── The shared rules ──

    [Theory]
    [InlineData(".agentos/screenshots/checkout-390-dark.png", true)]
    [InlineData("shots\\home-1280-light.JPG", true)]
    [InlineData("a.webp", true)]
    [InlineData("../x.png", false)]
    [InlineData("/etc/x.png", false)]
    [InlineData("C:/x.png", false)]
    [InlineData("~/x.png", false)]
    [InlineData(".agentos/files/logo.png", false)]   // a ticket's own files never go back
    [InlineData("shot.svg", false)]                  // could run script in our origin
    [InlineData("notes.txt", false)]
    [InlineData("con.png", false)]
    public void Rules_TryCheck(string path, bool ok)
    {
        RunUploadRules.TryCheck(path, out var normalized, out var error).ShouldBe(ok);
        if (ok) { normalized.ShouldNotContain("\\"); error.ShouldBeNull(); }
        else { error.ShouldNotBeNullOrWhiteSpace(); }
    }

    [Fact]
    public void Rules_LooksLikeImage_ChecksTheSignatureAgainstTheExtension()
    {
        RunUploadRules.LooksLikeImage("a.png", Png(20)).ShouldBeTrue();
        RunUploadRules.LooksLikeImage("a.jpg", [0xFF, 0xD8, 0xFF, 0xE0]).ShouldBeTrue();
        RunUploadRules.LooksLikeImage("a.webp", Encoding.ASCII.GetBytes("RIFF0000WEBPVP8 ")).ShouldBeTrue();
        RunUploadRules.LooksLikeImage("a.png", Encoding.UTF8.GetBytes("<svg onload=alert(1)>")).ShouldBeFalse();
        RunUploadRules.LooksLikeImage("a.jpg", Png(20)).ShouldBeFalse();
        RunUploadRules.LooksLikeImage("a.png", PngHead.AsSpan(0, 4)).ShouldBeFalse();
    }

    // ── Runner: what it picks from its run folder ──

    private string Write(string relative, byte[] content)
    {
        var full = Path.Join(_runDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    private static readonly RunUploads.Limits Caps = new(RunUploadRules.MaxFiles, RunUploadRules.MaxFileBytes, RunUploadRules.MaxTotalBytes);

    [Fact]
    public void Runner_ListedPaths_FromTheVerdictJson()
    {
        RunUploads.ListedPaths("""
            Done.
            {"verdict":"pass","criteria":[],"screenshots":[{"path":".agentos/screenshots/a-390-light.png"},".agentos/screenshots/b.png",{"x":1}],"summary":"ok"}
            """).ShouldBe([".agentos/screenshots/a-390-light.png", ".agentos/screenshots/b.png"]);
        RunUploads.ListedPaths("no json here").ShouldBeEmpty();
        RunUploads.ListedPaths("""{"verdict":"pass","screenshots":"nope"}""").ShouldBeEmpty();
        RunUploads.ListedPaths(null).ShouldBeEmpty();
    }

    // Send what the runner would send for these candidates, recording every piece; the fake server refuses `refuse` paths.
    private async Task<(RunUploads.Outcome Outcome, List<RunUploadChunk> Chunks)> SendAsync(IReadOnlyList<string> listed, RunUploads.Limits? caps = null,
        string? refuse = null)
    {
        var chunks = new List<RunUploadChunk>();
        var outcome = await RunUploads.SendAsync(_runDir, RunUploads.Candidates(_runDir, listed), caps ?? Caps, c =>
        {
            chunks.Add(c);
            return Task.FromResult(refuse is not null && c.Path.Contains(refuse, StringComparison.Ordinal) ? "Refused: no." : null);
        });
        return (outcome, chunks);
    }

    [Fact]
    public async Task Runner_SendsImagesInsideTheRunFolder_RefusesTheRest()
    {
        Write(".agentos/screenshots/checkout-390-dark.png", Png(64));
        Write(".agentos/screenshots/fake-390-light.png", Encoding.UTF8.GetBytes("not an image at all"));
        Write("repo/secret.txt", Encoding.UTF8.GetBytes("x"));
        var outside = Path.Join(_root, "outside.png");
        File.WriteAllBytes(outside, Png(64));

        var (outcome, chunks) = await SendAsync(
            [".agentos/screenshots/checkout-390-dark.png", ".agentos/screenshots/fake-390-light.png", "../outside.png", outside.Replace('\\', '/'),
             "repo/secret.txt", ".agentos/screenshots/missing.png", ".agentos/screenshots/checkout-390-dark.png"]);

        outcome.Sent.ShouldBe([".agentos/screenshots/checkout-390-dark.png"]);    // the duplicate counts once
        outcome.Problems.Count.ShouldBe(5);
        outcome.Problems.ShouldContain(r => r.Contains("../outside.png", StringComparison.Ordinal));
        chunks.ShouldAllBe(c => c.Path == ".agentos/screenshots/checkout-390-dark.png");   // nothing else was ever read out
    }

    [Fact]
    public async Task Runner_RespectsTheCaps()
    {
        for (var i = 0; i < 4; i++) { Write($".agentos/screenshots/s{i}-390-light.png", Png(100)); }
        Write(".agentos/screenshots/big-1280-dark.png", Png(5000));
        var listed = Enumerable.Range(0, 4).Select(i => $".agentos/screenshots/s{i}-390-light.png").Append(".agentos/screenshots/big-1280-dark.png").ToList();

        var tooMany = (await SendAsync(listed, new RunUploads.Limits(2, 10_000, 10_000))).Outcome;
        tooMany.Sent.Count.ShouldBe(2);
        tooMany.Problems.ShouldContain(r => r.Contains("more than 2 files", StringComparison.Ordinal));
        var tooBig = (await SendAsync(listed, new RunUploads.Limits(10, 1000, 10_000))).Outcome;
        tooBig.Sent.Count.ShouldBe(4);
        tooBig.Problems.ShouldContain(r => r.Contains("bigger than 1000", StringComparison.Ordinal));
        var tooMuch = (await SendAsync(listed, new RunUploads.Limits(10, 10_000, 250))).Outcome;
        tooMuch.Sent.Count.ShouldBe(2);
        tooMuch.Problems.ShouldContain(r => r.Contains("over the run's 250 bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runner_NothingListed_FallsBackToTheScreenshotsFolder_TopLevelOnly()
    {
        Write(".agentos/screenshots/b-1280-light.png", Png(10));
        Write(".agentos/screenshots/a-390-dark.png", Png(10));
        Write(".agentos/screenshots/deeper/c.png", Png(10));
        Write(".agentos/screenshots/readme.md", Encoding.UTF8.GetBytes("#"));

        (await SendAsync([])).Outcome.Sent.ShouldBe([".agentos/screenshots/a-390-dark.png", ".agentos/screenshots/b-1280-light.png"]);
    }

    [Fact]
    public async Task Runner_ALinkedFolder_IsRefused_NothingReadThroughIt()
    {
        var outside = Path.Join(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Join(outside, "x-390-light.png"), Png(10));
        Directory.CreateDirectory(Path.Join(_runDir, ".agentos"));
        try { Directory.CreateSymbolicLink(Path.Join(_runDir, ".agentos", "screenshots"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine can't create symbolic links (Windows without Developer Mode).");
        }

        var (outcome, chunks) = await SendAsync([".agentos/screenshots/x-390-light.png"]);

        outcome.Sent.ShouldBeEmpty();
        outcome.Problems.ShouldHaveSingleItem();
        chunks.ShouldBeEmpty();
        RunUploads.LinkBetween(_runDir, Path.Join(_runDir, ".agentos", "screenshots")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Runner_ALinkedFile_IsRefused()
    {
        var outside = Path.Join(_root, "secret.png");
        File.WriteAllBytes(outside, Png(10));
        Directory.CreateDirectory(Path.Join(_runDir, ".agentos", "screenshots"));
        try { File.CreateSymbolicLink(Path.Join(_runDir, ".agentos", "screenshots", "x-390-light.png"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine can't create symbolic links (Windows without Developer Mode).");
        }

        var (outcome, chunks) = await SendAsync([".agentos/screenshots/x-390-light.png"]);
        outcome.Sent.ShouldBeEmpty();
        chunks.ShouldBeEmpty();
    }

    [Fact]
    public async Task Runner_ChecksAndReadsOneHandle_TheFileCantBeSwappedOrGrowMeanwhile()
    {
        // The file is held open from the check to the last piece: while it is sent, nobody can replace or delete it, and
        // it never sends more than the length the checks saw.
        var full = Write(".agentos/screenshots/a-390-light.png", Png(100));
        var swapped = false;
        var grown = false;

        var outcome = await RunUploads.SendAsync(_runDir, [".agentos/screenshots/a-390-light.png"], Caps, c =>
        {
            try { File.Delete(full); swapped = true; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { File.AppendAllText(full, "more"); grown = true; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            c.Data.Length.ShouldBe(100);
            return Task.FromResult<string?>(null);
        });

        outcome.Sent.ShouldHaveSingleItem();
        swapped.ShouldBeFalse();
        grown.ShouldBeFalse();
    }

    [Fact]
    public async Task Runner_Send_InPiecesOfTheChunkSize_StopsAFileTheServerRefuses()
    {
        var big = Png(RunUploadRules.ChunkBytes + 10);
        big[^1] = 42;
        Write(".agentos/screenshots/a-390-light.png", big);
        Write(".agentos/screenshots/b-390-dark.png", Png(10));

        var (outcome, sent) = await SendAsync([".agentos/screenshots/a-390-light.png", ".agentos/screenshots/b-390-dark.png"], refuse: "b-390");

        sent.Count.ShouldBe(3);
        sent[0].Offset.ShouldBe(0);
        sent[0].Data.Length.ShouldBe(RunUploadRules.ChunkBytes);
        sent[0].Last.ShouldBeFalse();
        sent[1].Offset.ShouldBe(RunUploadRules.ChunkBytes);
        sent[1].Last.ShouldBeTrue();
        sent[0].Data.Concat(sent[1].Data).ShouldBe(big);
        outcome.Problems.ShouldBe(["Refused: no."]);
        outcome.Sent.ShouldBe([".agentos/screenshots/a-390-light.png"]);
    }

    // ── Broker: heard only from the dispatched connection, every piece re-checked ──

    private static (InProcessRemoteAgentBroker Broker, Task<RemoteExecResult> Run, IDisposable[] Regs) Dispatched(RunCollect limits)
    {
        var broker = new InProcessRemoteAgentBroker();
        var mine = broker.RegisterRunner("conn-mine", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var other = broker.RegisterRunner("conn-other", new RunnerConnection(Guid.NewGuid(), "t2", "u2"));
        broker.ExpectUploads("req-1", limits);
        var run = broker.DispatchAsync(new RemoteExecRequest("req-1", "s", "u", "m", Collect: limits), new RunnerTarget("t1", "u1"), TimeSpan.FromSeconds(5));
        return (broker, run, [mine, other]);
    }

    [Fact]
    public async Task Broker_OnlyTheDispatchedConnection_AfterDispatch()
    {
        var notYet = new InProcessRemoteAgentBroker();
        using var reg = notYet.RegisterRunner("conn-mine", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        notYet.ExpectUploads("req-1", RunCollect.Screenshots);
        notYet.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("a.png", 0, Png(10), true)).ShouldNotBeNull();   // not dispatched yet

        var (broker, run, regs) = Dispatched(RunCollect.Screenshots);
        broker.UploadRunFile("conn-other", "req-1", new RunUploadChunk("a.png", 0, Png(10), true)).ShouldNotBeNull();
        broker.UploadRunFile("conn-mine", "req-unknown", new RunUploadChunk("a.png", 0, Png(10), true)).ShouldNotBeNull();
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("a.png", 0, Png(10), true)).ShouldBeNull();

        broker.Complete(new RemoteExecResult("req-1", true, "ok", null));
        await run;
        broker.TakeUploads("req-1").ShouldHaveSingleItem().Path.ShouldBe("a.png");
        broker.TakeUploads("req-1").ShouldBeEmpty();                                                            // forgotten
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("b.png", 0, Png(10), true)).ShouldNotBeNull();
        foreach (var r in regs) { r.Dispose(); }
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("C:/x.png")]
    [InlineData(".agentos/files/logo.png")]
    [InlineData("x.svg")]
    [InlineData("shots\\a.png")]      // must arrive normalised
    [InlineData("shots//a.png")]
    public async Task Broker_RefusesABadPath(string path)
    {
        var (broker, run, regs) = Dispatched(RunCollect.Screenshots);
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk(path, 0, Png(10), true))!.ShouldStartWith("Refused");
        broker.Complete(new RemoteExecResult("req-1", true, "ok", null));
        await run;
        broker.TakeUploads("req-1").ShouldBeEmpty();
        foreach (var r in regs) { r.Dispose(); }
    }

    [Fact]
    public async Task Broker_PiecesInOrder_SizesCountAndSignatureChecked()
    {
        var (broker, run, regs) = Dispatched(new RunCollect(2, 100, 150));
        // Out of order: a piece that doesn't continue the file drops it.
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("a.png", 0, Png(10), false)).ShouldBeNull();
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("a.png", 50, Png(10), true))!.ShouldContain("in order");
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("b.png", 5, Png(10), true))!.ShouldContain("start at the beginning");
        // Per file.
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("big.png", 0, Png(101), true))!.ShouldContain("at most 100 bytes");
        // A renamed text file.
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("fake.png", 0, Encoding.UTF8.GetBytes("hello hello"), true))!.ShouldContain("isn't the image");
        // Two good files fill the count; the total cap and the count both hold.
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("c.png", 0, Png(80), true)).ShouldBeNull();
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("d.png", 0, Png(80), true))!.ShouldContain("at most 150 bytes");
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("e.png", 0, Png(60), true)).ShouldBeNull();
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("f.png", 0, Png(10), true))!.ShouldContain("at most 2 files");
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("c.png", 0, Png(10), true))!.ShouldContain("already sent");
        // A piece above the wire cap.
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("g.png", 0, new byte[RunUploadRules.ChunkBytes + 1], false))!.ShouldContain("a piece is at most");

        broker.Complete(new RemoteExecResult("req-1", true, "ok", null));
        await run;
        broker.TakeUploads("req-1").Select(u => u.Path).ShouldBe(["c.png", "e.png"]);
        foreach (var r in regs) { r.Dispose(); }
    }

    [Fact]
    public void Broker_ExpectUploads_NeverAboveTheHardCaps()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-mine", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        broker.ExpectUploads("req-1", new RunCollect(10_000, long.MaxValue, long.MaxValue));
        _ = broker.DispatchAsync(new RemoteExecRequest("req-1", "s", "u", "m"), new RunnerTarget("t1", "u1"), TimeSpan.FromSeconds(5));
        for (var i = 0; i < RunUploadRules.MaxFiles; i++)
        {
            broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk($"s{i}.png", 0, Png(10), true)).ShouldBeNull();
        }
        broker.UploadRunFile("conn-mine", "req-1", new RunUploadChunk("one-too-many.png", 0, Png(10), true))!.ShouldContain($"at most {RunUploadRules.MaxFiles} files");
        broker.Complete(new RemoteExecResult("req-1", true, "ok", null));
    }

    // ── Client: what arrived is stored under the run's design scope, for the run's tenant ──

    [Fact]
    public void Broker_InFlightBudget_RefusesNewSlotsWhenFull_AndFreesThemWhenTaken()
    {
        var broker = new InProcessRemoteAgentBroker();
        var perRun = RunCollect.Screenshots.MaxTotalBytes;
        var fits = (int)(InProcessRemoteAgentBroker.MaxInFlightUploadBytes / perRun);
        for (var i = 0; i < fits; i++) { broker.ExpectUploads($"req-{i}", RunCollect.Screenshots).ShouldBeTrue(); }
        broker.ReservedUploadBytes.ShouldBe(fits * perRun);

        broker.ExpectUploads("req-over", RunCollect.Screenshots).ShouldBeFalse();     // the server-wide budget is spent
        broker.UploadRunFile("conn", "req-over", new RunUploadChunk("a.png", 0, Png(10), true)).ShouldNotBeNull();
        broker.ExpectUploads("req-small", new RunCollect(1, 10, 10)).ShouldBeFalse();  // nothing left at all

        broker.TakeUploads("req-0").ShouldBeEmpty();
        broker.ExpectUploads("req-over", RunCollect.Screenshots).ShouldBeTrue();      // freed by the run that ended
        broker.ExpectUploads("req-over", RunCollect.Screenshots).ShouldBeTrue();      // re-expecting the same id doesn't leak
        broker.ReservedUploadBytes.ShouldBe(fits * perRun);
        for (var i = 1; i < fits; i++) { broker.TakeUploads($"req-{i}"); }
        broker.TakeUploads("req-over");
        broker.ReservedUploadBytes.ShouldBe(0);
    }

    [Fact]
    public async Task Client_BudgetSpent_AsksForNothing_AndTheRunStillAnswers()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        for (var i = 0; broker.ExpectUploads($"busy-{i}", RunCollect.Screenshots); i++) { }
        RemoteExecRequest? sent = null;
        broker.Dispatched += d => { sent = d.Request; broker.Complete(new RemoteExecResult(d.Request.Id, true, "ok", null)); };
        await using var services = new ServiceCollection().AddSingleton<IScopedFileStore>(new InMemoryScopedFileStore()).BuildServiceProvider();

        LlmResponse response;
        using (AmbientIdentity.Push("t1", "u1"))
        {
            response = await new RemoteAgentLlmClient(broker, services, NullLogger<RemoteAgentLlmClient>.Instance)
                .SendAsync(new LlmRequest("s", "u", "m") { Uploads = new RunUploadTarget("design:x:1:0", RunCollect.Screenshots) });
        }

        sent!.Collect.ShouldBeNull();
        response.Content.ShouldBe("ok");
        response.Uploaded.ShouldBeEmpty();
    }

    [Fact]
    public async Task Client_StoresUnderThePathPrefix_SoTwoReposNeverCollide()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        broker.Dispatched += d =>
        {
            broker.UploadRunFile(d.ConnectionId, d.Request.Id, new RunUploadChunk(".agentos/screenshots/home-390-light.png", 0, Png(20), true)).ShouldBeNull();
            broker.Complete(new RemoteExecResult(d.Request.Id, true, "ok", null));
        };
        var store = new InMemoryScopedFileStore();
        await using var services = new ServiceCollection().AddSingleton<IScopedFileStore>(store).BuildServiceProvider();
        var client = new RemoteAgentLlmClient(broker, services, NullLogger<RemoteAgentLlmClient>.Instance);
        var scope = AgentOs.Domain.Sessions.DesignScope.For(Guid.NewGuid(), 7, 0);
        var repoA = AgentOs.Domain.Sessions.DesignCapture.PrefixFor(Guid.NewGuid());
        var repoB = AgentOs.Domain.Sessions.DesignCapture.PrefixFor(Guid.NewGuid());

        using (AmbientIdentity.Push("t1", "u1"))
        {
            foreach (var prefix in new[] { repoA, repoB })
            {
                await client.SendAsync(new LlmRequest("s", "u", "m") { Uploads = new RunUploadTarget(scope, RunCollect.Screenshots) { PathPrefix = prefix } });
            }
        }

        (await store.ListAsync("t1", scope)).Select(f => f.Path).Order(StringComparer.Ordinal)
            .ShouldBe(new[] { repoA, repoB }.Select(p => p + ".agentos/screenshots/home-390-light.png").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Client_StoresWhatTheRunnerSentBack_InTheTenantsDesignScope()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        RemoteExecRequest? sent = null;
        var shot = Png(300);
        broker.Dispatched += d =>
        {
            sent = d.Request;
            broker.UploadRunFile(d.ConnectionId, d.Request.Id, new RunUploadChunk(".agentos/screenshots/checkout-390-dark.png", 0, shot, true)).ShouldBeNull();
            broker.Complete(new RemoteExecResult(d.Request.Id, true, """{"verdict":"pass"}""", null));
        };
        var store = new InMemoryScopedFileStore();
        await using var services = new ServiceCollection().AddSingleton<IScopedFileStore>(store).BuildServiceProvider();
        var client = new RemoteAgentLlmClient(broker, services, NullLogger<RemoteAgentLlmClient>.Instance);
        var scope = AgentOs.Domain.Sessions.DesignScope.For(Guid.NewGuid(), 7, 0);

        LlmResponse response;
        using (AmbientIdentity.Push("t1", "u1"))
        {
            response = await client.SendAsync(new LlmRequest("s", "u", "m") { Uploads = new RunUploadTarget(scope, RunCollect.Screenshots) });
        }

        sent!.Collect.ShouldBe(RunCollect.Screenshots);
        sent.UserPrompt.ShouldNotContain(scope);                       // the scope stays on the server
        var stored = response.Uploaded.ShouldHaveSingleItem();
        stored.Path.ShouldBe(".agentos/screenshots/checkout-390-dark.png");
        stored.TenantId.ShouldBe("t1");
        stored.Sha256.ShouldBe(RunFile.Hash(shot));
        (await store.GetAsync("t1", scope, stored.Path))!.Content.ShouldBe(shot);
        (await store.ListAsync("t2", scope)).ShouldBeEmpty();         // another tenant sees nothing
        broker.TakeUploads(sent.Id).ShouldBeEmpty();                   // released
    }

    [Fact]
    public async Task Client_NotAsked_NoCollect_NothingStored()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        RemoteExecRequest? sent = null;
        string? refusal = null;
        broker.Dispatched += d =>
        {
            sent = d.Request;
            refusal = broker.UploadRunFile(d.ConnectionId, d.Request.Id, new RunUploadChunk("a.png", 0, Png(10), true));
            broker.Complete(new RemoteExecResult(d.Request.Id, true, "ok", null));
        };
        var store = new InMemoryScopedFileStore();
        await using var services = new ServiceCollection().AddSingleton<IScopedFileStore>(store).BuildServiceProvider();

        LlmResponse response;
        using (AmbientIdentity.Push("t1", "u1"))
        {
            response = await new RemoteAgentLlmClient(broker, services, NullLogger<RemoteAgentLlmClient>.Instance).SendAsync(new LlmRequest("s", "u", "m"));
        }

        sent!.Collect.ShouldBeNull();
        refusal.ShouldNotBeNull();
        response.Uploaded.ShouldBeEmpty();
    }

    // ── The wire contract ──

    [Fact]
    public void Contract_ExecuteWithCollect_RoundTripsIntoTheRunnersShapes()
    {
        var protocol = new JsonHubProtocol();
        var request = new RemoteExecRequest("id-1", "sys", "user", "m", Collect: new RunCollect(16, 5_000_000, 40_000_000));

        var message = RoundTrip(protocol, new InvocationMessage("Execute", [request]), typeof(RunnerExecRequest));

        var received = message.ShouldBeOfType<InvocationMessage>().Arguments.ShouldHaveSingleItem().ShouldBeOfType<RunnerExecRequest>();
        received.Collect.ShouldBe(new RunUploads.Limits(16, 5_000_000, 40_000_000));
    }

    [Fact]
    public void Contract_UploadChunk_RoundTripsBytes()
    {
        var protocol = new JsonHubProtocol();
        var chunk = new RunUploadChunk(".agentos/screenshots/a.png", 262144, [0, 1, 2, 250, 255], true);

        var message = RoundTrip(protocol, new InvocationMessage("UploadRunFile", [chunk]), typeof(RunUploadChunk));

        var back = message.ShouldBeOfType<InvocationMessage>().Arguments.ShouldHaveSingleItem().ShouldBeOfType<RunUploadChunk>();
        back.Path.ShouldBe(chunk.Path);
        back.Offset.ShouldBe(262144);
        back.Data.ShouldBe(chunk.Data);
        back.Last.ShouldBeTrue();
    }

    [Fact]
    public void Hub_AcceptsAnUploadPiece_AboveSignalRsDefault()
    {
        // One piece as base64 JSON: ~4/3 of the chunk plus the envelope — over the 32 KB default, under this hub's cap.
        var envelope = (RunUploadRules.ChunkBytes * 4 / 3) + 1024;
        envelope.ShouldBeGreaterThan(32 * 1024);
        RemoteAgentHub.MaxReceiveBytes.ShouldBeGreaterThan(envelope);
    }

    // The runner's own record (src/AgentOs.RemoteAgent/Program.cs) — mirrored, as that exe isn't referenced.
    private sealed record RunnerExecRequest(string Id, string SystemPrompt, string UserPrompt, string Model, string? Cli = null, string? Autonomy = null,
        IReadOnlyList<RunFiles.Entry>? Files = null, RunUploads.Limits? Collect = null);

    private static HubMessage RoundTrip(JsonHubProtocol protocol, HubMessage message, Type argumentType)
    {
        var writer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(message, writer);
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
        protocol.TryParseMessage(ref sequence, new Binder(argumentType), out var parsed).ShouldBeTrue();
        return parsed!;
    }

    private sealed class Binder(Type type) : Microsoft.AspNetCore.SignalR.IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => [type];
        public Type GetReturnType(string invocationId) => type;
        public Type GetStreamItemType(string streamId) => type;
    }
}
