// Ticket files (ADR-0015) — delivery to a member's computer: the broker serves a staged file only to the connection the
// run was dispatched to, in pieces; RemoteAgentLlmClient sends the manifest with the dispatch and records the delivery
// as evidence; the server-driven path (write_files) names the folder; and the wire contract survives SignalR's JSON
// protocol into the runner's own shapes (the dispatch contract round-trip).

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Tools;
using AgentOs.Modules.RemoteAgent;
using AgentOs.RemoteAgent;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class RunFileDeliveryTests
{
    private static RunFile File(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new RunFile(path, bytes, RunFile.Hash(bytes), "text/plain");
    }

    // ── Broker: staged files, served only to the dispatched connection ──────────────────────────

    [Fact]
    public async Task Broker_StagedFiles_ServedOnlyToTheConnectionTheRunWentTo_InPieces()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var mine = broker.RegisterRunner("conn-mine", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        using var other = broker.RegisterRunner("conn-other", new RunnerConnection(Guid.NewGuid(), "t2", "u2"));
        var big = new byte[RunFileChunk.MaxBytes + 10];
        Random.Shared.NextBytes(big);
        broker.StageFiles("req-1", [new RunFile("big.bin", big, RunFile.Hash(big), "application/octet-stream"), File("a.txt", "hi")]);

        broker.ReadRunFile("conn-mine", "req-1", 0, 0).ShouldBeNull();   // not dispatched yet: nobody may fetch
        var run = broker.DispatchAsync(new RemoteExecRequest("req-1", "s", "u", "m"), new RunnerTarget("t1", "u1"), TimeSpan.FromSeconds(5));

        broker.ReadRunFile("conn-other", "req-1", 0, 0).ShouldBeNull();   // another tenant's runner
        broker.ReadRunFile("conn-mine", "req-unknown", 0, 0).ShouldBeNull();
        broker.ReadRunFile("conn-mine", "req-1", 5, 0).ShouldBeNull();

        var first = broker.ReadRunFile("conn-mine", "req-1", 0, 0).ShouldNotBeNull();
        first.Data.Length.ShouldBe(RunFileChunk.MaxBytes);
        first.Last.ShouldBeFalse();
        var second = broker.ReadRunFile("conn-mine", "req-1", 0, RunFileChunk.MaxBytes).ShouldNotBeNull();
        second.Data.Length.ShouldBe(10);
        second.Last.ShouldBeTrue();
        first.Data.Concat(second.Data).ShouldBe(big);
        broker.ReadRunFile("conn-mine", "req-1", 0, 0).ShouldBeNull();    // fetched to the end: released

        broker.CompleteFromServer(new RemoteExecResult("req-1", true, "done", null));
        await run;
        broker.UnstageFiles("req-1").ShouldBe(1);                          // one of two fetched to the end
        broker.ReadRunFile("conn-mine", "req-1", 1, 0).ShouldBeNull();
    }

    // ── Client: the manifest rides the dispatch, the delivery is evidence ───────────────────────

    [Fact]
    public async Task Client_FilesOnTheRequest_TravelAsManifest_AndAreRecordedAsEvidence()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        RemoteExecRequest? sent = null;
        // The fake runner: fetch every file to the end, then answer.
        broker.Dispatched += d =>
        {
            sent = d.Request;
            for (var i = 0; i < d.Request.Files!.Count; i++)
            {
                long offset = 0;
                while (broker.ReadRunFile(d.ConnectionId, d.Request.Id, i, offset) is { } chunk)
                {
                    offset += chunk.Data.Length;
                    if (chunk.Last) { break; }
                }
            }
            broker.CompleteFromServer(new RemoteExecResult(d.Request.Id, true, "ok", null));
        };
        var log = new RecordingLog();
        var feed = new InMemorySessionRunFeed();
        await using var services = new ServiceCollection().AddSingleton<IToolInvocationLog>(log).AddSingleton<ISessionRunFeed>(feed).BuildServiceProvider();
        var client = new RemoteAgentLlmClient(broker, services, NullLogger<RemoteAgentLlmClient>.Instance);
        var session = Guid.NewGuid();
        var files = new[] { File("logo.png", "png"), File("skills/x/SKILL.md", "# x") };

        using (AmbientIdentity.Push("t1", "u1", session))
        {
            await client.SendAsync(new LlmRequest("s", "u", "m") { Autonomy = WorkspaceAutonomy.Scoped, Files = files });
        }

        sent!.Files.ShouldNotBeNull().Select(f => f.Path).ShouldBe(["logo.png", "skills/x/SKILL.md"]);
        sent.Files![0].Sha256.ShouldBe(files[0].Sha256);
        sent.Files[0].Size.ShouldBe(3);
        var evidence = log.Entries.ShouldHaveSingleItem();
        evidence.ToolName.ShouldBe("ticket_files");
        evidence.TenantId.ShouldBe("t1");
        evidence.CallId.ShouldBe(sent.Id);                                  // the run id
        evidence.SessionId.ShouldBe(session.ToString("D"));
        evidence.IsError.ShouldBeFalse();
        evidence.Input.ShouldContain(files[1].Sha256);
        evidence.Input.ShouldContain("skills/x/SKILL.md");
        evidence.Output.ShouldContain("Delivered 2 of 2");
    }

    [Fact]
    public async Task Client_NoFiles_SendsNoManifest_AndRecordsNothing()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        RemoteExecRequest? sent = null;
        broker.Dispatched += d => { sent = d.Request; broker.CompleteFromServer(new RemoteExecResult(d.Request.Id, true, "ok", null)); };
        var log = new RecordingLog();
        await using var services = new ServiceCollection().AddSingleton<IToolInvocationLog>(log).BuildServiceProvider();
        var client = new RemoteAgentLlmClient(broker, services, NullLogger<RemoteAgentLlmClient>.Instance);

        using (AmbientIdentity.Push("t1", "u1"))
        {
            await client.SendAsync(new LlmRequest("s", "u", "m"));
        }

        sent!.Files.ShouldBeNull();
        log.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Client_RunnerFetchedNothing_EvidenceSaysSo()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        // An older runner ignores the manifest.
        broker.Dispatched += d => broker.CompleteFromServer(new RemoteExecResult(d.Request.Id, true, "ok", null));
        var log = new RecordingLog();
        await using var services = new ServiceCollection().AddSingleton<IToolInvocationLog>(log).BuildServiceProvider();
        var client = new RemoteAgentLlmClient(broker, services, NullLogger<RemoteAgentLlmClient>.Instance);

        using (AmbientIdentity.Push("t1", "u1"))
        {
            await client.SendAsync(new LlmRequest("s", "u", "m") { Files = [File("a.txt", "a")] });
        }

        var evidence = log.Entries.ShouldHaveSingleItem();
        evidence.IsError.ShouldBeTrue();
        evidence.Output.ShouldContain("Delivered 0 of 1");
    }

    // ── Server-driven path: write_files names the folder ────────────────────────────────────────

    [Fact]
    public async Task Delivery_WriteFilesToolCall_ReturnsTheFolder_AndIsEvidence()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        RunnerToolCall? call = null;
        broker.ToolCallDispatched += d =>
        {
            call = d.Call;
            while (broker.ReadRunFile(d.ConnectionId, d.Call.RequestId, 0, 0) is { Last: false }) { }
            broker.CompleteToolCallFromServer(new RunnerToolResult(d.Call.RequestId, d.Call.ToolCallId, true, "C:/runs/20261025-files-x", null));
        };
        var log = new RecordingLog();
        await using var services = new ServiceCollection().AddSingleton<IToolInvocationLog>(log).BuildServiceProvider();
        var delivery = new RunnerFileDeliveryService(broker, services);

        var result = await delivery.DeliverAsync("t1", "u1", [File("logo.png", "png")]);

        result.Ok.ShouldBeTrue(result.Error);
        result.Folder.ShouldBe("C:/runs/20261025-files-x");
        call!.ToolName.ShouldBe("write_files");
        call.JsonInput.ShouldContain("logo.png");
        log.Entries.ShouldHaveSingleItem().Output.ShouldContain("Delivered 1 of 1");
    }

    [Fact]
    public async Task Delivery_NoComputer_FailsWithAReason()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var result = await new RunnerFileDeliveryService(new InProcessRemoteAgentBroker(), services).DeliverAsync("t1", "u1", [File("a.txt", "a")]);
        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("No computer");
    }

    // ── The wire contract: server shapes → SignalR JSON protocol → the runner's shapes ──────────

    [Fact]
    public void Contract_ExecuteWithFiles_RoundTripsIntoTheRunnersShapes()
    {
        var protocol = new JsonHubProtocol();
        var request = new RemoteExecRequest("id-1", "sys", "user", "m", "claude", WorkspaceAutonomy.Scoped,
            [new RunFileEntry("art/logo.png", 3, new string('a', 64))]);

        var message = RoundTrip(protocol, new InvocationMessage("Execute", [request]), typeof(RunnerExecRequest));

        var received = message.ShouldBeOfType<InvocationMessage>().Arguments.ShouldHaveSingleItem().ShouldBeOfType<RunnerExecRequest>();
        received.Id.ShouldBe("id-1");
        received.Autonomy.ShouldBe(WorkspaceAutonomy.Scoped);
        var entry = received.Files.ShouldNotBeNull().ShouldHaveSingleItem();
        entry.ShouldBe(new RunFiles.Entry("art/logo.png", 3, new string('a', 64)));
    }

    [Fact]
    public void Contract_Chunk_RoundTripsBytes()
    {
        var protocol = new JsonHubProtocol();
        var bytes = new byte[] { 0, 1, 2, 250, 255 };

        var message = RoundTrip(protocol, CompletionMessage.WithResult("inv-1", new RunFileChunk(bytes, true)), typeof(RunFiles.Chunk));

        var chunk = ((CompletionMessage)message).Result.ShouldBeOfType<RunFiles.Chunk>();
        chunk.Data.ShouldBe(bytes);
        chunk.Last.ShouldBeTrue();
    }

    [Fact]
    public void Contract_WriteFilesInput_ParsesOnTheRunner()
    {
        var json = JsonSerializer.Serialize(new { files = new[] { new RunFileEntry("a.txt", 1, "ab") } });
        var parsed = JsonSerializer.Deserialize<RunnerWriteFilesInput>(json, CaseInsensitive);
        parsed!.Files.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new RunFiles.Entry("a.txt", 1, "ab"));
    }

    // What the runner parses a write_files input with (Program.ToolJson).
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    // The runner's own records (src/AgentOs.RemoteAgent/Program.cs) — mirrored, as that exe isn't referenced.
    private sealed record RunnerExecRequest(string Id, string SystemPrompt, string UserPrompt, string Model, string? Cli = null, string? Autonomy = null,
        IReadOnlyList<RunFiles.Entry>? Files = null);

    private sealed record RunnerWriteFilesInput(IReadOnlyList<RunFiles.Entry>? Files);

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

    private sealed class RecordingLog : IToolInvocationLog
    {
        public List<ToolInvocationEvidence> Entries { get; } = [];

        public Task AppendAsync(ToolInvocationEvidence entry, CancellationToken cancellationToken = default)
        {
            lock (Entries) { Entries.Add(entry); }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(string tenantId, int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolInvocationEvidence>>(Entries.Where(e => e.TenantId == tenantId).Take(limit).ToList());
    }
}
