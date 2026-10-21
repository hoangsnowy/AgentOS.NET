// "Try the app": the recipe rules (shared by server and runner), the broker's preview routing (a report counts only
// from the connection the preview went to), the runner host's input guards, and the Board-side preview state.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Preview;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.RemoteAgent;
using AgentOs.Web.Services.Preview;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using ServerPreviewStart = AgentOs.Modules.RemoteAgent.PreviewStart;

namespace AgentOs.Tests.Sessions;

public class TryTheAppTests
{
    // ── PreviewRecipe ──

    [Fact]
    public void TryParse_ValidRecipe_ReadsCommandUrlAndTimeout()
    {
        var recipe = PreviewRecipe.TryParse("""{ "command": "dotnet run --project infra/App.AppHost", "url": "https://localhost:5173/", "readyTimeoutSeconds": 900 }""", out var error);

        error.ShouldBeNull();
        recipe.ShouldNotBeNull();
        recipe.Command.ShouldBe("dotnet run --project infra/App.AppHost");
        recipe.Url.AbsoluteUri.ShouldBe("https://localhost:5173/");
        recipe.ReadyTimeoutSeconds.ShouldBe(900);
    }

    [Fact]
    public void TryParse_NoTimeout_UsesDefault()
    {
        PreviewRecipe.TryParse("""{ "command": "npm start", "url": "http://127.0.0.1:3000" }""", out _)!
            .ReadyTimeoutSeconds.ShouldBe(PreviewRecipe.DefaultReadyTimeoutSeconds);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("[]", "JSON object")]
    [InlineData("{ not json", "not valid JSON")]
    [InlineData("""{ "url": "http://localhost:3000" }""", "no \"command\"")]
    [InlineData("""{ "command": "npm start\nrm -rf /", "url": "http://localhost:3000" }""", "single line")]
    [InlineData("""{ "command": "npm start" }""", "\"url\"")]
    [InlineData("""{ "command": "npm start", "url": "ftp://localhost/x" }""", "\"url\"")]
    [InlineData("""{ "command": "npm start", "url": "https://example.com" }""", "localhost")]
    [InlineData("""{ "command": "npm start", "url": "http://localhost:3000", "readyTimeoutSeconds": 5 }""", "readyTimeoutSeconds")]
    [InlineData("""{ "command": "npm start", "url": "http://localhost:3000", "readyTimeoutSeconds": "600" }""", "readyTimeoutSeconds")]
    public void TryParse_UnusableRecipe_ReturnsNullWithReason(string json, string reasonPart)
    {
        PreviewRecipe.TryParse(json, out var error).ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain(reasonPart);
    }

    [Fact]
    public void TryParse_OverlongCommand_IsRefused()
    {
        var json = $$"""{ "command": "{{new string('a', 401)}}", "url": "http://localhost:3000" }""";
        PreviewRecipe.TryParse(json, out var error).ShouldBeNull();
        error!.ShouldContain("longer");
    }

    [Theory]
    [InlineData("http://localhost:1", true)]
    [InlineData("http://app.dev.localhost:1", true)]
    [InlineData("http://127.0.0.1:1", true)]
    [InlineData("http://[::1]:1", true)]
    [InlineData("http://localhost.example.com:1", false)]
    [InlineData("http://10.0.0.5:1", false)]
    public void IsLocal_OnlyLoopbackHosts(string url, bool expected) =>
        PreviewRecipe.IsLocal(new Uri(url)).ShouldBe(expected);

    [Fact]
    public void Example_IsAValidRecipe() =>
        PreviewRecipe.TryParse(PreviewRecipe.Example, out _).ShouldNotBeNull();

    // ── Broker routing ──

    private static ServerPreviewStart Start(string id = "p1") =>
        new(id, "octo", "app", "main", "npm start", "http://localhost:3000/", 600);

    [Fact]
    public void StartPreview_NoRunner_ReturnsFalse()
    {
        var broker = new InProcessRemoteAgentBroker();
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1")).ShouldBeFalse();
    }

    [Fact]
    public void StartPreview_PushesToTheMembersConnection()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var _ = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        PreviewCommand? pushed = null;
        broker.PreviewCommanded += c => pushed = c;

        broker.StartPreview(Start(), new RunnerTarget("t1", "u1")).ShouldBeTrue();

        pushed.ShouldNotBeNull();
        pushed.ConnectionId.ShouldBe("conn-a");
        pushed.Start!.PreviewId.ShouldBe("p1");
    }

    [Fact]
    public void ReportPreview_FromAnotherConnection_IsIgnored()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t2", "u2"));
        var reports = new List<PreviewStatus>();
        broker.PreviewReported += reports.Add;
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1"));

        broker.ReportPreview("conn-b", "p1", PreviewStates.Ready, "forged");
        broker.ReportPreview("conn-a", "p1", "Bogus", null);
        broker.ReportPreview("conn-a", "unknown", PreviewStates.Ready, null);
        reports.ShouldBeEmpty();

        broker.ReportPreview("conn-a", "p1", PreviewStates.Ready, "http://localhost:3000/");
        reports.ShouldHaveSingleItem().State.ShouldBe(PreviewStates.Ready);
    }

    [Fact]
    public void ReportPreview_FinalState_ForgetsThePreview()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var reports = new List<PreviewStatus>();
        broker.PreviewReported += reports.Add;
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1"));

        broker.ReportPreview("conn-a", "p1", PreviewStates.Failed, "boom");
        broker.ReportPreview("conn-a", "p1", PreviewStates.Ready, null);

        reports.ShouldHaveSingleItem().State.ShouldBe(PreviewStates.Failed);
    }

    [Fact]
    public void StopPreview_PushesStopToTheRunningConnection()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var pushed = new List<PreviewCommand>();
        broker.PreviewCommanded += pushed.Add;
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1"));

        broker.StopPreview("p1");
        broker.StopPreview("never-started");

        pushed.Count.ShouldBe(2);
        pushed[1].StopPreviewId.ShouldBe("p1");
        pushed[1].ConnectionId.ShouldBe("conn-a");
    }

    [Fact]
    public void RunnerDisconnect_ReportsItsPreviewsStopped()
    {
        var broker = new InProcessRemoteAgentBroker();
        var registration = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var reports = new List<PreviewStatus>();
        broker.PreviewReported += reports.Add;
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1"));

        registration.Dispose();

        reports.ShouldHaveSingleItem().State.ShouldBe(PreviewStates.Stopped);
    }

    // ── Runner host guards ──

    [Theory]
    [InlineData("octo-org", true)]
    [InlineData("My.Repo_2", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("-x", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a b", false)]
    public void IsSafeName_GitHubNamesOnly(string name, bool expected) =>
        PreviewHost.IsSafeName(name).ShouldBe(expected);

    [Theory]
    [InlineData("main", true)]
    [InlineData("release/1.2", true)]
    [InlineData("--upload-pack=evil", false)]
    [InlineData("a..b", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void IsSafeBranch_RejectsOptionsAndTraversal(string branch, bool expected) =>
        PreviewHost.IsSafeBranch(branch).ShouldBe(expected);

    [Fact]
    public void ShellFor_RunsTheLineInTheCheckout()
    {
        var psi = PreviewHost.ShellFor("npm run dev -- --host \"x y\"", "C:/work");
        psi.WorkingDirectory.ShouldBe("C:/work");
        psi.UseShellExecute.ShouldBeFalse();
        if (OperatingSystem.IsWindows())
        {
            psi.FileName.ShouldBe("cmd.exe");
            psi.Arguments.ShouldBe("/d /s /c \"npm run dev -- --host \"x y\"\"");
        }
        else
        {
            psi.ArgumentList.ShouldBe(["-c", "npm run dev -- --host \"x y\""]);
        }
    }

    [Fact]
    public async Task IsListening_TrueOnlyWhileSomethingListens()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            (await PreviewHost.IsListeningAsync(new Uri($"http://127.0.0.1:{port}"), CancellationToken.None)).ShouldBeTrue();
        }
        finally
        {
            listener.Stop();
        }
        (await PreviewHost.IsListeningAsync(new Uri($"http://127.0.0.1:{port}"), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Start_UnsafeRepoName_FailsWithoutRunningAnything()
    {
        var reports = new List<(string Id, string State)>();
        var host = new PreviewHost(System.IO.Path.GetTempPath(), (id, state, _) => { lock (reports) { reports.Add((id, state)); } return Task.CompletedTask; });

        host.Start(new AgentOs.RemoteAgent.PreviewStart("p1", "..", "app", "main", "npm start", "http://localhost:3000/", 600));
        await Task.Yield();

        reports.ShouldBe([("p1", "Failed")]);
    }

    // ── Board-side state ──

    private static (PreviewService Service, InProcessRemoteAgentBroker Broker) Service()
    {
        var broker = new InProcessRemoteAgentBroker();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new PreviewService(broker, scopes), broker);
    }

    private static WorkspaceRepoEntity Repo() => new() { Id = Guid.NewGuid(), Owner = "octo", Repo = "app", DefaultBranch = "main" };

    private static PreviewRecipe Recipe() => PreviewRecipe.TryParse("""{ "command": "npm start", "url": "http://localhost:3000" }""", out _)!;

    [Fact]
    public void Start_NoComputer_KeepsNoState()
    {
        var (service, _) = Service();
        using var _s = service;
        var repo = Repo();

        service.Start("t1", "u1", Guid.NewGuid(), repo, Recipe()).ShouldBe(PreviewStartResult.NoComputer);
        service.Get("t1", repo.Id).ShouldBeNull();
    }

    [Fact]
    public void Start_ThenReports_TrackStateAndHoldTheBoard()
    {
        var (service, broker) = Service();
        using var _s = service;
        using var conn = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        string? sent = null;
        broker.PreviewCommanded += c => sent ??= c.Start?.PreviewId;
        var board = Guid.NewGuid();
        var repo = Repo();
        var changes = 0;
        service.Changed += t => { if (t == "t1") { changes++; } };

        service.Start("t1", "u1", board, repo, Recipe()).ShouldBe(PreviewStartResult.Started);
        service.IsActiveOnBoard("t1", board).ShouldBeTrue();
        service.IsActiveOnBoard("t2", board).ShouldBeFalse();

        broker.ReportPreview("conn-a", sent!, PreviewStates.Ready, "http://localhost:3000/");
        service.Get("t1", repo.Id)!.State.ShouldBe(PreviewStates.Ready);

        broker.ReportPreview("conn-a", sent!, PreviewStates.Stopped, null);
        service.Get("t1", repo.Id)!.IsActive.ShouldBeFalse();
        service.IsActiveOnBoard("t1", board).ShouldBeFalse();
        changes.ShouldBe(3);
    }

    [Fact]
    public void RecipeTicket_IsAnApprovedChoreWithCriteria()
    {
        var draft = PreviewService.RecipeTicket(Repo());
        draft.AiReady.ShouldBeTrue();
        draft.Labels.ShouldContain("type:chore");
        draft.Title.ShouldContain(PreviewRecipe.FilePath);
        draft.Body.ShouldContain("## Acceptance criteria");
        draft.Body.ShouldContain("\"command\"");
    }
}
