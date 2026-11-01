// "Try the app" keeps running across a runner restart: the runner's state file round-trips, a restarted runner re-attaches
// a preview only when its pid is still the same process (live / dead / recycled pid), Stop kills a re-attached preview's
// whole tree, the hub's re-announce contract (server ↔ runner records, tenant/member guard, grace, stop-on-return), the
// Board re-attaches the same card, and the per-project testing hold setting.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Preview;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.RemoteAgent;
using AgentOs.Web.Services.Preview;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using RunnerAnnouncement = AgentOs.RemoteAgent.PreviewAnnouncement;
using RunnerPreviewStart = AgentOs.RemoteAgent.PreviewStart;
using ServerAnnouncement = AgentOs.Modules.RemoteAgent.PreviewAnnouncement;
using ServerPreviewStart = AgentOs.Modules.RemoteAgent.PreviewStart;

namespace AgentOs.Tests.Sessions;

public sealed class PreviewReattachTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "agentos-preview-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    public PreviewReattachTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); } }
            catch (InvalidOperationException ex) { _ = ex; } // exited meanwhile
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex; // a file still held by a dying process: the temp folder is cleaned by the OS
        }
    }

    private static PreviewRecord Record(string id = "p1", int pid = 1, long ticks = 0, bool ready = true, bool ownGroup = false) =>
        new(id, "octo", "app", "npm start", "http://localhost:3000/", 3000, 600, 120,
            Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
            pid, ticks, ownGroup, new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero), ready);

    // A detached app like the runner starts it: own group, output in the log, in a temp checkout.
    private Process StartApp(string id, string unixCommand, string windowsCommand)
    {
        var dir = Path.Join(_root, "octo-app");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(PreviewStore.Folder(_root));
        var psi = PreviewProcess.ShellFor(OperatingSystem.IsWindows() ? windowsCommand : unixCommand, dir, PreviewStore.LogPath(_root, id));
        var process = Process.Start(psi)!;
        _started.Add(process);
        return process;
    }

    private PreviewHost Host(List<(string Id, string State, string? Message)> reports) =>
        new(_root, (id, state, message) => { lock (reports) { reports.Add((id, state, message)); } return Task.CompletedTask; }, inUse: _ => true);

    private static string Sleeper => OperatingSystem.IsWindows() ? "ping -n 300 127.0.0.1 >nul" : "sleep 300";

    // ── State on disk ──

    [Fact]
    public void PreviewStore_SaveThenLoad_RoundTripsEveryField()
    {
        var record = Record(pid: 4242, ticks: 638_000_000_000_000_000, ready: false, ownGroup: true);

        PreviewStore.Save(_root, record);

        PreviewStore.Load(_root).ShouldHaveSingleItem().ShouldBe(record);
        File.Exists(Path.Join(PreviewStore.Folder(_root), "p1.json.tmp")).ShouldBeFalse();
    }

    [Fact]
    public void PreviewStore_Load_DropsAnUnreadableOrMisnamedFile()
    {
        PreviewStore.Save(_root, Record("good"));
        File.WriteAllText(Path.Join(PreviewStore.Folder(_root), "broken.json"), "{ not json");
        File.Copy(Path.Join(PreviewStore.Folder(_root), "good.json"), Path.Join(PreviewStore.Folder(_root), "other.json"));

        PreviewStore.Load(_root).ShouldHaveSingleItem().PreviewId.ShouldBe("good");
        Directory.GetFiles(PreviewStore.Folder(_root), "*.json").Select(Path.GetFileName).ShouldBe(["good.json"]);
    }

    [Theory]
    [InlineData("0f8fa1c2d3e4", true)]
    [InlineData("", false)]
    [InlineData("../x", false)]
    [InlineData("a/b", false)]
    public void PreviewStore_IsSafeId_OnlyFileNameSafeIds(string id, bool expected) => PreviewStore.IsSafeId(id).ShouldBe(expected);

    [Fact]
    public void PreviewStore_Delete_RemovesStateAndLog()
    {
        PreviewStore.Save(_root, Record());
        File.WriteAllText(PreviewStore.LogPath(_root, "p1"), "line 1\nline 2\nline 3\n");
        PreviewStore.Tail(_root, "p1", 2).ShouldBe("line 2\nline 3");

        PreviewStore.Delete(_root, "p1");

        PreviewStore.Load(_root).ShouldBeEmpty();
        File.Exists(PreviewStore.LogPath(_root, "p1")).ShouldBeFalse();
    }

    // ── Re-attach after a runner restart ──

    [Fact]
    public async Task Restore_LivePid_ReattachesAndAnnouncesIt()
    {
        var app = StartApp("p1", Sleeper, Sleeper);
        PreviewStore.Save(_root, Record(pid: app.Id, ticks: app.StartTime.ToUniversalTime().Ticks));
        var reports = new List<(string, string, string?)>();
        var host = Host(reports);

        host.Restore().ShouldBeEmpty();

        var (running, gone) = host.Announcements();
        gone.ShouldBeEmpty();
        var announced = running.ShouldHaveSingleItem();
        announced.PreviewId.ShouldBe("p1");
        announced.State.ShouldBe("Ready");
        announced.Url.ShouldBe("http://localhost:3000/");
        announced.BoardId.ShouldBe(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        await Task.Delay(200);
        app.HasExited.ShouldBeFalse(); // re-attaching never touches the app
        host.Detach();
    }

    [Fact]
    public async Task Restore_DeadPid_ForgetsItAndReportsItGone()
    {
        var app = StartApp("p1", "sleep 0.5", "ping -n 1 127.0.0.1 >nul");
        var ticks = app.StartTime.ToUniversalTime().Ticks;
        await app.WaitForExitAsync();
        PreviewStore.Save(_root, Record(pid: app.Id, ticks: ticks));
        var host = Host([]);

        host.Restore().ShouldBe(["p1"]);

        PreviewStore.Load(_root).ShouldBeEmpty();
        var (running, gone) = host.Announcements();
        running.ShouldBeEmpty();
        gone.ShouldBe(["p1"]);
    }

    [Fact]
    public void Restore_RecycledPid_LeavesThatProcessAlone()
    {
        // The pid now belongs to another program: its start time differs from the one on disk.
        var other = StartApp("p1", Sleeper, Sleeper);
        var wrongTicks = other.StartTime.ToUniversalTime().AddHours(-1).Ticks;
        PreviewStore.Save(_root, Record(pid: other.Id, ticks: wrongTicks, ownGroup: !OperatingSystem.IsWindows()));
        var host = Host([]);

        host.Restore().ShouldBe(["p1"]);
        host.Stop("p1"); // nothing to stop: it was never re-attached

        other.HasExited.ShouldBeFalse();
        PreviewProcess.IsSame(other.Id, wrongTicks).ShouldBeFalse();
        PreviewProcess.IsSame(other.Id, other.StartTime.ToUniversalTime().Ticks).ShouldBeTrue();
    }

    [Fact]
    public async Task Stop_AfterReattach_KillsTheWholeProcessTree()
    {
        // The app starts a background child (a dev server's watcher, a database) and then waits — like a real AppHost.
        var app = StartApp("p1",
            "sleep 300 & echo $! > child.pid; wait",
            "start /b ping -n 300 127.0.0.1 >nul & ping -n 300 127.0.0.1 >nul");
        var childPidFile = Path.Join(_root, "octo-app", "child.pid");
        var record = Record(pid: app.Id, ticks: app.StartTime.ToUniversalTime().Ticks, ownGroup: PreviewProcess.StartsOwnGroup(app.StartInfo));
        PreviewStore.Save(_root, record);
        var childPid = 0;
        if (!OperatingSystem.IsWindows())
        {
            for (var i = 0; i < 50 && childPid == 0; i++)
            {
                await Task.Delay(100);
                if (File.Exists(childPidFile)) { _ = int.TryParse(File.ReadAllText(childPidFile).Trim(), CultureInfo.InvariantCulture, out childPid); }
            }
            childPid.ShouldBeGreaterThan(0);
            IsGone(childPid).ShouldBeFalse();
        }
        // A new runner (the old one restarted) takes it back, then the person presses Stop.
        var reports = new List<(string Id, string State, string? Message)>();
        var host = Host(reports);
        host.Restore().ShouldBeEmpty();

        host.Stop("p1");

        (await WaitAsync(() => app.HasExited)).ShouldBeTrue();
        if (childPid > 0) { (await WaitAsync(() => IsGone(childPid))).ShouldBeTrue(); }
        reports.ShouldContain(r => r.Id == "p1" && r.State == "Stopped");
        PreviewStore.Load(_root).ShouldBeEmpty();
        host.Announcements().Running.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reattached_AppThatEnds_IsReportedStoppedAndForgotten()
    {
        var app = StartApp("p1", "sleep 1", "ping -n 2 127.0.0.1 >nul");
        PreviewStore.Save(_root, Record(pid: app.Id, ticks: app.StartTime.ToUniversalTime().Ticks));
        var reports = new List<(string Id, string State, string? Message)>();
        var host = Host(reports);
        host.Restore().ShouldBeEmpty();

        (await WaitAsync(() => { lock (reports) { return reports.Any(r => r.State == "Stopped"); } }, seconds: 20)).ShouldBeTrue();

        PreviewStore.Load(_root).ShouldBeEmpty();
        host.Announcements().Running.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 120)]
    [InlineData(5, 120)]
    [InlineData(30, 30)]
    [InlineData(5000, 120)]
    public void IdleMinutesFor_ServerValueInRange_ElseTheDefault(int fromServer, int expected) =>
        PreviewHost.IdleMinutesFor(fromServer).ShouldBe(expected);

    [Fact]
    public void IsIdle_OnlyAfterTheIdleTime()
    {
        var start = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        PreviewHost.IsIdle(start, start.AddMinutes(29), 30).ShouldBeFalse();
        PreviewHost.IsIdle(start, start.AddMinutes(30), 30).ShouldBeTrue();
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 10)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (condition()) { return true; }
            await Task.Delay(100);
        }
        return condition();
    }

    // Gone = no such process, or a zombie waiting for its parent to reap it (it runs nothing any more).
    private static bool IsGone(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                return stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z');
            }
            catch (IOException) { return true; }
        }
        return PreviewProcess.StartTicks(pid) is null;
    }

    // ── Hub re-announce contract ──

    private static ServerPreviewStart Start(string id = "p1") =>
        new(id, "octo", "app", "main", "npm start", "http://localhost:3000/", 600, 120,
            Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static ServerAnnouncement Announced(string id = "p1", string state = PreviewStates.Ready) =>
        new(id, "octo", "app", "npm start", "http://localhost:3000/", DateTimeOffset.UtcNow,
            Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), state);

    [Fact]
    public void Hub_AnnouncePreviews_IsTheMethodTheRunnerCalls()
    {
        var method = typeof(RemoteAgentHub).GetMethod(nameof(RemoteAgentHub.AnnouncePreviews))!;
        method.GetParameters().Select(p => p.ParameterType).ShouldBe([typeof(ServerAnnouncement[]), typeof(string[])]);
    }

    [Fact]
    public void Announcement_RunnerRecord_ReadsAsTheServersRecord()
    {
        // SignalR's JSON protocol: camelCase names, by property — the two copies of the record must agree.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var sent = new RunnerAnnouncement("p1", "octo", "app", "npm start", "http://localhost:3000/",
            new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero), Guid.NewGuid(), Guid.NewGuid(), "Ready");

        var read = JsonSerializer.Deserialize<ServerAnnouncement>(JsonSerializer.Serialize(sent, web), web)!;

        read.ShouldBe(new ServerAnnouncement(sent.PreviewId, sent.Owner, sent.Repo, sent.Command, sent.Url, sent.StartedAt,
            sent.BoardId, sent.RepoId, sent.State));
    }

    [Fact]
    public void Start_ServerRecord_ReadsAsTheRunnersRecord_WithIdleBoardAndRepo()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var read = JsonSerializer.Deserialize<RunnerPreviewStart>(JsonSerializer.Serialize(Start(), web), web)!;

        read.IdleMinutes.ShouldBe(120);
        read.BoardId.ShouldBe(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        read.RepoId.ShouldBe(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        // An older server that doesn't send them yet: the runner falls back to its defaults.
        var old = JsonSerializer.Deserialize<RunnerPreviewStart>(
            """{"previewId":"p1","owner":"o","repo":"r","branch":"main","command":"c","url":"http://localhost:1/","readyTimeoutSeconds":60}""", web)!;
        old.IdleMinutes.ShouldBe(0);
        old.BoardId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void Announce_AfterARestartAndReparing_ReattachesToTheNewConnection()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var reports = new List<PreviewStatus>();
        var reattached = new List<PreviewReattach>();
        var pushed = new List<PreviewCommand>();
        broker.PreviewReported += reports.Add;
        broker.PreviewReattached += reattached.Add;
        broker.PreviewCommanded += pushed.Add;
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1"));
        a.Dispose(); // the runner restarts (a new pairing = a new runner id)
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));

        broker.AnnouncePreviews("conn-b", [Announced()], []);

        reattached.ShouldHaveSingleItem().TenantId.ShouldBe("t1");
        broker.ReportPreview("conn-a", "p1", PreviewStates.Failed, "stale connection");
        reports.ShouldBeEmpty();
        broker.StopPreview("p1");
        pushed[^1].ShouldBe(new PreviewCommand("conn-b", null, "p1"));
        broker.ReportPreview("conn-b", "p1", PreviewStates.Stopped, null);
        reports.ShouldHaveSingleItem().State.ShouldBe(PreviewStates.Stopped);
    }

    [Fact]
    public void Announce_FromAnotherTenantOrMember_IsIgnored()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        using var other = broker.RegisterRunner("conn-x", new RunnerConnection(Guid.NewGuid(), "t2", "u1"));
        using var colleague = broker.RegisterRunner("conn-y", new RunnerConnection(Guid.NewGuid(), "t1", "u2"));
        var reattached = new List<PreviewReattach>();
        broker.PreviewReattached += reattached.Add;
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1"));
        a.Dispose();

        broker.AnnouncePreviews("conn-x", [Announced()], []);
        broker.AnnouncePreviews("conn-y", [Announced()], []);
        broker.AnnouncePreviews("conn-y", [Announced("../bad")], []);
        broker.AnnouncePreviews("unknown-connection", [Announced()], []);

        reattached.ShouldBeEmpty();
    }

    [Fact]
    public void Announce_SameRunnerWithoutIt_OrListedGone_ReportsStopped()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runnerId = Guid.NewGuid();
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(runnerId, "t1", "u1"));
        var reports = new List<PreviewStatus>();
        broker.PreviewReported += reports.Add;
        broker.StartPreview(Start("p1"), new RunnerTarget("t1", "u1"));
        broker.StartPreview(Start("p2"), new RunnerTarget("t1", "u1"));
        a.Dispose();
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1")); // re-paired

        broker.AnnouncePreviews("conn-b", [], ["p1"]);
        reports.ShouldHaveSingleItem().PreviewId.ShouldBe("p1");

        using var c = broker.RegisterRunner("conn-c", new RunnerConnection(runnerId, "t1", "u1")); // the same runner again
        broker.AnnouncePreviews("conn-c", [], []);
        reports.Select(r => (r.PreviewId, r.State)).ShouldBe([("p1", PreviewStates.Stopped), ("p2", PreviewStates.Stopped)]);
    }

    [Fact]
    public void Stop_WhileTheComputerIsAway_ReportsStoppedAndStopsItOnReturn()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var reports = new List<PreviewStatus>();
        var pushed = new List<PreviewCommand>();
        var reattached = new List<PreviewReattach>();
        broker.PreviewReported += reports.Add;
        broker.PreviewCommanded += pushed.Add;
        broker.PreviewReattached += reattached.Add;
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1"));
        a.Dispose();

        broker.StopPreview("p1");
        reports.ShouldHaveSingleItem().State.ShouldBe(PreviewStates.Stopped);

        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        broker.AnnouncePreviews("conn-b", [Announced()], []);

        pushed[^1].ShouldBe(new PreviewCommand("conn-b", null, "p1"));
        reattached.ShouldBeEmpty();
    }

    // ── Board side: the same card carries on ──

    private static (PreviewService Service, InProcessRemoteAgentBroker Broker) Service()
    {
        var broker = new InProcessRemoteAgentBroker();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new PreviewService(broker, scopes), broker);
    }

    private static PreviewRecipe Recipe() => PreviewRecipe.TryParse("""{ "command": "npm start", "url": "http://localhost:3000" }""", out _)!;

    [Fact]
    public void Reattach_KnownPreview_KeepsTheCardRunningWithTheSameUrl()
    {
        var (service, broker) = Service();
        using var _s = service;
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        ServerPreviewStart? sent = null;
        broker.PreviewCommanded += c => sent ??= c.Start;
        var board = Guid.NewGuid();
        var repo = new WorkspaceRepoEntity { Id = Guid.NewGuid(), Owner = "octo", Repo = "app", DefaultBranch = "main" };
        service.Start("t1", "u1", board, repo, Recipe());
        sent!.IdleMinutes.ShouldBe(AutopilotSettings.DefaultPreviewIdleMinutes);
        sent.BoardId.ShouldBe(board);
        sent.RepoId.ShouldBe(repo.Id);
        broker.ReportPreview("conn-a", sent.PreviewId, PreviewStates.Ready, "http://localhost:3000/");

        a.Dispose(); // runner restart
        service.Get("t1", repo.Id)!.IsActive.ShouldBeTrue();
        service.IsActiveOnBoard("t1", board).ShouldBeTrue();
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        broker.AnnouncePreviews("conn-b", [Announced(sent.PreviewId) with { BoardId = board, RepoId = repo.Id }], []);

        var state = service.Get("t1", repo.Id)!;
        state.Id.ShouldBe(sent.PreviewId);
        state.State.ShouldBe(PreviewStates.Ready);
        state.Url.ShouldBe("http://localhost:3000/");
        service.Stop("t1", repo.Id);
        broker.ReportPreview("conn-b", sent.PreviewId, PreviewStates.Stopped, null);
        service.Get("t1", repo.Id)!.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void Reattach_UnknownPreview_RebuildsTheCard_AfterAWebRestart()
    {
        var (service, broker) = Service();
        using var _s = service;
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var changed = 0;
        service.Changed += _ => changed++;

        broker.AnnouncePreviews("conn-b", [Announced("p9")], []);

        var state = service.Get("t1", Guid.Parse("22222222-2222-2222-2222-222222222222")).ShouldNotBeNull();
        state.Id.ShouldBe("p9");
        state.UserId.ShouldBe("u1");
        state.BoardId.ShouldBe(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        state.IsActive.ShouldBeTrue();
        changed.ShouldBe(1);
    }

    [Fact]
    public void Reattach_PreviewTheBoardCantPlace_IsStopped()
    {
        var (service, broker) = Service();
        using var _s = service;
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var pushed = new List<PreviewCommand>();
        broker.PreviewCommanded += pushed.Add;

        broker.AnnouncePreviews("conn-b", [Announced("p9") with { BoardId = Guid.Empty }], []);

        pushed.ShouldHaveSingleItem().StopPreviewId.ShouldBe("p9");
    }

    // ── The testing hold is a per-project setting ──

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Other project", true)]
    [InlineData("TeacherHub", false)]
    [InlineData(" other , teacherhub ", false)]
    [InlineData("33333333-3333-3333-3333-333333333333", false)]
    public void HoldsTestingWhileTrying_HoldsUnlessTheProjectIsListed(string? setting, bool holds) =>
        AutopilotSettings.HoldsTestingWhileTrying(setting, "TeacherHub", Guid.Parse("33333333-3333-3333-3333-333333333333")).ShouldBe(holds);

    [Fact]
    public void Describe_DeclaresTheHoldAndTheIdleStop_InSettingsAutopilot()
    {
        var all = new AutopilotSettings().Describe().ToDictionary(d => d.Key);

        var hold = all[AutopilotSettings.TestWhileTryingKey];
        hold.Category.ShouldBe(AutopilotSettings.Category);
        hold.Kind.ShouldBe(SettingKind.Text);
        hold.DefaultValue.ShouldBeNull(); // nothing listed = every project holds testing, as before
        hold.Help!.ShouldStartWith("Testing can run while you try the app when the app keeps its data separately for each copy.");

        var idle = all[AutopilotSettings.PreviewIdleMinutesKey];
        idle.Key.ShouldBe("Preview:IdleMinutes");
        idle.DefaultValue.ShouldBe("120");
        idle.Kind.ShouldBe(SettingKind.WholeNumber);
    }

    [Fact]
    public async Task ReadPreviewIdleMinutes_UnsetIsTheDefault_AndAValueIsClamped()
    {
        (await AutopilotSettings.ReadPreviewIdleMinutesAsync(new FakeSettings(null), "t1")).ShouldBe(120);
        (await AutopilotSettings.ReadPreviewIdleMinutesAsync(new FakeSettings("45"), "t1")).ShouldBe(45);
        (await AutopilotSettings.ReadPreviewIdleMinutesAsync(new FakeSettings("1"), "t1")).ShouldBe(10);
        (await AutopilotSettings.ReadPreviewIdleMinutesAsync(new FakeSettings("99999"), "t1")).ShouldBe(1440);
    }

    private sealed class FakeSettings(string? idle) : ISettings
    {
        public long Version => 0;

        public ValueTask<SettingValue> GetAsync(string key, string? tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SettingValue(key == AutopilotSettings.PreviewIdleMinutesKey ? idle : null, SettingSource.Tenant));

        public ValueTask<IReadOnlyList<SettingView>> ListAsync(string? tenantId, string? category = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SettingWriteResult> SetAsync(string key, string? value, SettingScope scope, string? tenantId, string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
