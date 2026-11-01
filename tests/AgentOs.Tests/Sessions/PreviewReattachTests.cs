// "Try the app" keeps running across a runner restart: the runner's state file round-trips, a restarted runner re-attaches
// a preview only when its pid is still the same process (live / dead / recycled pid / not the shell it started), Stop kills
// a re-attached preview's whole tree, an unusable state never leaves an app running unseen, the hub's re-announce contract
// (server ↔ runner records, tenant/member guard, grace, stop-on-return), the Board re-attaches the same card — and rebuilds
// one only for a repo of that board in the tenant's own store, with a local url — and the per-project testing hold setting.

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
using AgentOs.Modules.Workspaces.Persistence;
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
    private static readonly Guid BoardA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RepoA = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _root = Path.Join(Path.GetTempPath(), "agentos-preview-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<LaunchedPreview> _started = [];

    public PreviewReattachTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var app in _started)
        {
            try { if (!app.Process.HasExited) { app.Process.Kill(entireProcessTree: true); } }
            catch (InvalidOperationException ex) { _ = ex; } // exited meanwhile
            app.Process.Dispose();
            app.Group?.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex; // a file still held by a dying process: the temp folder is cleaned by the OS
        }
    }

    private static string NewId() => "p" + Guid.NewGuid().ToString("N");

    private static PreviewRecord Record(string id = "p1", int pid = 1, long ticks = 0, bool ready = true, bool ownGroup = false, bool ownJob = false) =>
        new(id, "octo", "app", "npm start", "http://localhost:3000/", 3000, 600, 120, BoardA, RepoA,
            pid, ticks, ownGroup, new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero), ready, ownJob);

    // The record the runner keeps for an app it launched.
    private static PreviewRecord RecordFor(string id, LaunchedPreview app) =>
        Record(id, app.Process.Id, app.StartTicks, ownGroup: app.OwnGroup, ownJob: app.OwnJob);

    // A detached app like the runner starts it: own group / job, output in the log, in a temp checkout.
    private LaunchedPreview StartApp(string id, string unixCommand, string windowsCommand)
    {
        var dir = Path.Join(_root, "octo-app");
        Directory.CreateDirectory(dir);
        PreviewStore.EnsureFolder(_root);
        var app = PreviewProcess.Launch(OperatingSystem.IsWindows() ? windowsCommand : unixCommand, dir, PreviewStore.LogPath(_root, id), id);
        _started.Add(app);
        return app;
    }

    private PreviewHost Host(List<(string Id, string State, string? Message)> reports) =>
        new(_root, (id, state, message) => { lock (reports) { reports.Add((id, state, message)); } return Task.CompletedTask; }, inUse: _ => true);

    private static string Sleeper => OperatingSystem.IsWindows() ? "ping -n 300 127.0.0.1 >nul" : "sleep 300";

    // ── State on disk ──

    [Fact]
    public void PreviewStore_SaveThenLoad_RoundTripsEveryField()
    {
        var record = Record(pid: 4242, ticks: 638_000_000_000_000_000, ready: false, ownGroup: true, ownJob: true);

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
        var unreadable = new List<PreviewRecord>();

        PreviewStore.Load(_root, unreadable).ShouldHaveSingleItem().PreviewId.ShouldBe("good");
        Directory.GetFiles(PreviewStore.Folder(_root), "*.json").Select(Path.GetFileName).ShouldBe(["good.json"]);
        unreadable.ShouldBeEmpty(); // nothing to recognise in "{ not json"; a misnamed copy is never acted on
    }

    [Fact]
    public void PreviewStore_Load_AnUnreadableFileThatStillNamesAProcess_IsHandedBackToStopIt()
    {
        PreviewStore.EnsureFolder(_root);
        File.WriteAllText(Path.Join(PreviewStore.Folder(_root), "p7.json"), """{ "Pid": 4242, "ProcessStartTicks": 99, "OwnGroup": true, "Owner": 5 }""");
        var unreadable = new List<PreviewRecord>();

        PreviewStore.Load(_root, unreadable).ShouldBeEmpty();

        var identity = unreadable.ShouldHaveSingleItem();
        (identity.PreviewId, identity.Pid, identity.ProcessStartTicks, identity.OwnGroup).ShouldBe(("p7", 4242, 99L, true));
        File.Exists(Path.Join(PreviewStore.Folder(_root), "p7.json")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("0f8fa1c2d3e4", true)]
    [InlineData("", false)]
    [InlineData("../x", false)]
    [InlineData("a/b", false)]
    public void PreviewStore_IsSafeId_OnlyFileNameSafeIds(string id, bool expected) => PreviewStore.IsSafeId(id).ShouldBe(expected);

    [Fact]
    public void PreviewStore_Delete_RemovesStateAndLogs()
    {
        PreviewStore.Save(_root, Record());
        File.WriteAllText(PreviewStore.LogPath(_root, "p1"), "line 1\nline 2\nline 3\n");
        File.WriteAllText(PreviewStore.LogPath(_root, "p1") + ".1", "older\n");
        PreviewStore.Tail(_root, "p1", 2).ShouldBe("line 2\nline 3");

        PreviewStore.Delete(_root, "p1");

        PreviewStore.Load(_root).ShouldBeEmpty();
        File.Exists(PreviewStore.LogPath(_root, "p1")).ShouldBeFalse();
        File.Exists(PreviewStore.LogPath(_root, "p1") + ".1").ShouldBeFalse();
    }

    [Fact]
    public void PreviewStore_EnsureFolder_IsForThisUserOnly()
    {
        if (OperatingSystem.IsWindows()) { return; } // under the user's own %LOCALAPPDATA% there
        var folder = PreviewStore.Folder(_root);
        Directory.CreateDirectory(folder); // an older runner made it with the default mode
        File.SetUnixFileMode(folder, (UnixFileMode)0b111_101_101);

        PreviewStore.EnsureFolder(_root);

        File.GetUnixFileMode(folder).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task CapLog_PastTheLimit_KeepsOneOlderPart_AndTheAppAppendsToTheEmptiedLog()
    {
        var id = NewId();
        StartApp(id,
            "i=0; while [ $i -lt 3000 ]; do echo \"line $i of the app\"; i=$((i+1)); sleep 0.02; done",
            "for /l %i in (1,1,3000) do @(echo line %i of the app& ping -n 1 -w 20 127.0.0.1 >nul)");
        var log = PreviewStore.LogPath(_root, id);
        (await WaitAsync(() => LogLength(log) > 2000)).ShouldBeTrue();

        PreviewStore.CapLog(_root, id, maxBytes: 1000).ShouldBeTrue();

        File.ReadAllText(log + ".1").ShouldContain("line 1 of the app");
        var lengthAfterRotation = LogLength(log);
        (await WaitAsync(() => LogLength(log) > lengthAfterRotation + 100)).ShouldBeTrue();
        var bytes = ReadShared(log);
        bytes[0].ShouldNotBe((byte)0); // appended at the new end, not at the old offset behind a run of zeros
        bytes.Length.ShouldBeLessThan(2000);
        PreviewStore.CapLog(_root, id, maxBytes: 1_000_000).ShouldBeFalse();
    }

    private static long LogLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // ── Process identity ──

    [Fact]
    public void ParseStat_CountsFieldsAfterTheLastParenthesis()
    {
        // The command name may hold spaces and parentheses; pgrp is field 5, the start time field 22.
        var line = "4242 (my (odd) app) S 1 4240 4240 0 -1 4194560 100 0 0 0 1 2 0 0 20 0 1 0 987654 12345678 300 18446744073709551615";

        var stat = PreviewProcess.ParseStat(line).ShouldNotBeNull();

        stat.State.ShouldBe('S');
        stat.Group.ShouldBe(4240);
        stat.StartJiffies.ShouldBe(987654);
        PreviewProcess.ParseStat("garbage").ShouldBeNull();
    }

    [Fact]
    public void StartTicks_OnLinux_IsTheRawStartTimeFromProc()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        var pid = Environment.ProcessId;
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var field22 = long.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19], CultureInfo.InvariantCulture);

        PreviewProcess.StartTicks(pid).ShouldBe(field22);
    }

    [Fact]
    public async Task IsSame_APidThatIsNoLongerTheShell_IsNotThePreview()
    {
        // The right pid and start time, but not the shell a preview starts with: never re-attached, never killed by pid.
        using var other = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "ping" : "sleep",
            OperatingSystem.IsWindows() ? "-n 300 127.0.0.1" : "300") { UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            var ticks = PreviewProcess.StartTicks(other.Id)!.Value;

            PreviewProcess.IsShell(other.Id).ShouldBeFalse();
            PreviewProcess.IsSame(other.Id, ticks).ShouldBeFalse();
            PreviewProcess.Kill(Record(pid: other.Id, ticks: ticks)).ShouldBeFalse();
            await Task.Delay(200);
            other.HasExited.ShouldBeFalse();
        }
        finally
        {
            other.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Launch_AJobNameThatAlreadyExists_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var id = NewId();
        StartApp(id, Sleeper, Sleeper);

        // A job of that name already exists (here: the first launch's) — it isn't one this launch created, so no app runs in it.
        Should.Throw<InvalidOperationException>(() => StartApp(id, Sleeper, Sleeper)).Message.ShouldContain("process group");
    }

    [Fact]
    public async Task Kill_AfterTheFirstProcessExited_StillEndsWhatTheAppLeftRunning()
    {
        // The shell starts a background child and exits; then the runner lets go (a restart). Stop must still reach the
        // child: through the named job (Windows) or the process group it left behind (Linux).
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) { return; }
        var id = NewId();
        var app = StartApp(id, "sleep 300 & echo $! > child.pid", "start /b ping -n 300 127.0.0.1 >nul");
        if (!OperatingSystem.IsWindows() && !app.OwnGroup) { return; } // no setsid on this box: no group to find
        var record = RecordFor(id, app);
        (await WaitAsync(() => app.Process.HasExited)).ShouldBeTrue();
        app.Group?.Dispose(); // the runner restarts: its handle to the job is gone
        var childPid = 0;
        if (OperatingSystem.IsWindows())
        {
            PreviewProcess.JobExists(id).ShouldBeTrue(); // the app's processes keep the job's name alive
        }
        else
        {
            var childFile = Path.Join(_root, "octo-app", "child.pid");
            (await WaitAsync(() => File.Exists(childFile) && File.ReadAllText(childFile).Trim().Length > 0)).ShouldBeTrue();
            childPid = int.Parse(File.ReadAllText(childFile).Trim(), CultureInfo.InvariantCulture);
            IsGone(childPid).ShouldBeFalse();
            PreviewProcess.OrphanedGroupRemains(app.Process.Id).ShouldBeTrue();
        }

        PreviewProcess.Kill(record).ShouldBeFalse(); // the first process was gone already

        if (OperatingSystem.IsWindows()) { (await WaitAsync(() => !PreviewProcess.JobExists(id))).ShouldBeTrue(); }
        else { (await WaitAsync(() => IsGone(childPid))).ShouldBeTrue(); }
    }

    // ── Re-attach after a runner restart ──

    [Fact]
    public async Task Restore_LivePid_ReattachesAndAnnouncesIt()
    {
        var id = NewId();
        var app = StartApp(id, Sleeper, Sleeper);
        PreviewStore.Save(_root, RecordFor(id, app));
        var reports = new List<(string, string, string?)>();
        var host = Host(reports);

        host.Restore().ShouldBeEmpty();

        var (running, gone) = host.Announcements();
        gone.ShouldBeEmpty();
        var announced = running.ShouldHaveSingleItem();
        announced.PreviewId.ShouldBe(id);
        announced.State.ShouldBe("Ready");
        announced.Url.ShouldBe("http://localhost:3000/");
        announced.BoardId.ShouldBe(BoardA);
        await Task.Delay(200);
        app.Process.HasExited.ShouldBeFalse(); // re-attaching never touches the app
        host.Detach();
    }

    [Fact]
    public async Task Restore_DeadPid_ForgetsItAndReportsItGone()
    {
        var id = NewId();
        var app = StartApp(id, "sleep 0.5", "ping -n 1 127.0.0.1 >nul");
        var record = RecordFor(id, app);
        await app.Process.WaitForExitAsync();
        PreviewStore.Save(_root, record);
        var host = Host([]);

        host.Restore().ShouldBe([id]);

        PreviewStore.Load(_root).ShouldBeEmpty();
        var (running, gone) = host.Announcements();
        running.ShouldBeEmpty();
        gone.ShouldBe([id]);
    }

    [Fact]
    public void Restore_RecycledPid_LeavesThatProcessAlone()
    {
        // The pid now belongs to another program: its start time differs from the one on disk.
        var id = NewId();
        var other = StartApp(id, Sleeper, Sleeper);
        var wrongTicks = other.StartTicks - (OperatingSystem.IsLinux() ? 100 : TimeSpan.TicksPerHour);
        PreviewStore.Save(_root, Record(id, other.Process.Id, wrongTicks, ownGroup: other.OwnGroup));
        var host = Host([]);

        host.Restore().ShouldBe([id]);
        host.Stop(id); // nothing to stop: it was never re-attached

        other.Process.HasExited.ShouldBeFalse();
        PreviewProcess.IsSame(other.Process.Id, wrongTicks).ShouldBeFalse();
        PreviewProcess.IsSame(other.Process.Id, other.StartTicks).ShouldBeTrue();
    }

    [Fact]
    public async Task Restore_AStateTheBoardCantPlace_StopsItsApp_WhenItIsStillThePreviewsOwn()
    {
        var id = NewId();
        var app = StartApp(id, Sleeper, Sleeper);
        PreviewStore.Save(_root, RecordFor(id, app) with { Url = "javascript:alert(1)" });
        var host = Host([]);

        host.Restore().ShouldBe([id]);

        (await WaitAsync(() => app.Process.HasExited)).ShouldBeTrue(); // not left running unseen
        PreviewStore.Load(_root).ShouldBeEmpty();
        host.Announcements().Running.ShouldBeEmpty();
    }

    [Fact]
    public async Task Restore_AStateTheBoardCantPlace_WithARecycledPid_TouchesNothing()
    {
        var id = NewId();
        var other = StartApp(id, Sleeper, Sleeper);
        var wrongTicks = other.StartTicks - (OperatingSystem.IsLinux() ? 100 : TimeSpan.TicksPerHour);
        PreviewStore.Save(_root, Record(id, other.Process.Id, wrongTicks, ownGroup: other.OwnGroup) with { BoardId = Guid.Empty });

        Host([]).Restore().ShouldBe([id]);

        await Task.Delay(300);
        other.Process.HasExited.ShouldBeFalse();
    }

    [Fact]
    public async Task Restore_AnUnreadableStateNamingTheLiveApp_StopsIt()
    {
        var id = NewId();
        var app = StartApp(id, Sleeper, Sleeper);
        PreviewStore.EnsureFolder(_root);
        File.WriteAllText(Path.Join(PreviewStore.Folder(_root), id + ".json"), string.Create(CultureInfo.InvariantCulture,
            $$"""{ "Pid": {{app.Process.Id}}, "ProcessStartTicks": {{app.StartTicks}}, "OwnGroup": {{(app.OwnGroup ? "true" : "false")}}, "Url": [] }"""));

        Host([]).Restore().ShouldBe([id]);

        (await WaitAsync(() => app.Process.HasExited)).ShouldBeTrue();
    }

    [Fact]
    public async Task StopAll_StopsEveryPreviewOnThisComputer_AndForgetsThem()
    {
        var first = NewId();
        var second = NewId();
        var a = StartApp(first, Sleeper, Sleeper);
        var b = StartApp(second, Sleeper, Sleeper);
        PreviewStore.Save(_root, RecordFor(first, a));
        PreviewStore.Save(_root, RecordFor(second, b) with { Repo = "other" });

        PreviewHost.StopAll(_root).ShouldBe(2);

        (await WaitAsync(() => a.Process.HasExited && b.Process.HasExited)).ShouldBeTrue();
        PreviewStore.Load(_root).ShouldBeEmpty();
        PreviewHost.StopAll(_root).ShouldBe(0);
    }

    [Fact]
    public async Task Stop_AfterReattach_KillsTheWholeProcessTree()
    {
        // The app starts a background child (a dev server's watcher, a database) and then waits — like a real AppHost.
        var id = NewId();
        var app = StartApp(id,
            "sleep 300 & echo $! > child.pid; wait",
            "start /b ping -n 300 127.0.0.1 >nul & ping -n 300 127.0.0.1 >nul");
        var childPidFile = Path.Join(_root, "octo-app", "child.pid");
        PreviewStore.Save(_root, RecordFor(id, app));
        app.Group?.Dispose(); // the runner that started it is gone
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

        host.Stop(id);

        (await WaitAsync(() => app.Process.HasExited)).ShouldBeTrue();
        if (childPid > 0) { (await WaitAsync(() => IsGone(childPid))).ShouldBeTrue(); }
        if (OperatingSystem.IsWindows()) { (await WaitAsync(() => !PreviewProcess.JobExists(id))).ShouldBeTrue(); } // the whole job went
        reports.ShouldContain(r => r.Id == id && r.State == "Stopped");
        PreviewStore.Load(_root).ShouldBeEmpty();
        host.Announcements().Running.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reattached_AppThatEnds_IsReportedStoppedAndForgotten()
    {
        var id = NewId();
        var app = StartApp(id, "sleep 1", "ping -n 2 127.0.0.1 >nul");
        PreviewStore.Save(_root, RecordFor(id, app));
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
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    // ── Hub re-announce contract ──

    private static ServerPreviewStart Start(string id = "p1") =>
        new(id, "octo", "app", "main", "npm start", "http://localhost:3000/", 600, 120, BoardA, RepoA);

    private static ServerAnnouncement Announced(string id = "p1", string state = PreviewStates.Ready) =>
        new(id, "octo", "app", "npm start", "http://localhost:3000/", DateTimeOffset.UtcNow, BoardA, RepoA, state);

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
        read.BoardId.ShouldBe(BoardA);
        read.RepoId.ShouldBe(RepoA);
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

    [Fact]
    public void Stop_WhileAway_IsRememberedFromWhenStopWasPressed_NotFromTheDisconnect()
    {
        var clock = new TryTheAppTests.ManualClock();
        var broker = new InProcessRemoteAgentBroker(clock);
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var pushed = new List<PreviewCommand>();
        var reattached = new List<PreviewReattach>();
        broker.PreviewCommanded += pushed.Add;
        broker.PreviewReattached += reattached.Add;
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1"));
        a.Dispose();
        clock.Advance(TimeSpan.FromMinutes(14));
        broker.StopPreview("p1"); // pressed just before the reconnect grace runs out

        clock.Advance(TimeSpan.FromMinutes(2)); // 16 min after the disconnect: the grace is over, the Stop is not
        broker.SweepDetachedPreviews();
        clock.Advance(TimeSpan.FromDays(3));
        broker.SweepDetachedPreviews();
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        broker.AnnouncePreviews("conn-b", [Announced()], []);

        pushed[^1].ShouldBe(new PreviewCommand("conn-b", null, "p1")); // the app closes when the computer is back
        reattached.ShouldBeEmpty();
    }

    [Fact]
    public void Stop_WhileAway_IsForgottenAfterAWeek()
    {
        var clock = new TryTheAppTests.ManualClock();
        var broker = new InProcessRemoteAgentBroker(clock);
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var pushed = new List<PreviewCommand>();
        var reattached = new List<PreviewReattach>();
        broker.PreviewCommanded += pushed.Add;
        broker.PreviewReattached += reattached.Add;
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1"));
        a.Dispose();
        broker.StopPreview("p1");

        clock.Advance(InProcessRemoteAgentBroker.StopOnReturnKeep);
        broker.SweepDetachedPreviews();
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        pushed.Clear();
        broker.AnnouncePreviews("conn-b", [Announced()], []);

        pushed.ShouldBeEmpty(); // forgotten: the app comes back on the Board instead, where it can be stopped again
        reattached.ShouldHaveSingleItem();
    }

    [Fact]
    public void Announce_GoneFromAnotherMembersComputer_DoesNotClearAStop()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var pushed = new List<PreviewCommand>();
        broker.PreviewCommanded += pushed.Add;
        broker.StartPreview(Start(), new RunnerTarget("t1", "u1"));
        a.Dispose();
        broker.StopPreview("p1");
        using var colleague = broker.RegisterRunner("conn-y", new RunnerConnection(Guid.NewGuid(), "t1", "u2"));

        broker.AnnouncePreviews("conn-y", [], ["p1"]); // not u1's computer: its "gone" counts for nothing

        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        broker.AnnouncePreviews("conn-b", [Announced()], []);
        pushed[^1].ShouldBe(new PreviewCommand("conn-b", null, "p1"));
    }

    // ── Board side: the same card carries on ──

    private static (PreviewService Service, InProcessRemoteAgentBroker Broker) Service(params WorkspaceRepoEntity[] repos)
    {
        var broker = new InProcessRemoteAgentBroker();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceRepository>(new FakeWorkspaces(repos));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new PreviewService(broker, scopes), broker);
    }

    // The repo the runner's announcement names: under BoardA, in tenant t1, octo/app.
    private static WorkspaceRepoEntity StoredRepo(string tenant = "t1", Guid? board = null, string owner = "octo", string repo = "app") =>
        new() { Id = RepoA, TenantId = tenant, WorkspaceId = board ?? BoardA, Owner = owner, Repo = repo, DefaultBranch = "main" };

    private static PreviewRecipe Recipe() => PreviewRecipe.TryParse("""{ "command": "npm start", "url": "http://localhost:3000" }""", out _)!;

    [Fact]
    public async Task Reattach_KnownPreview_KeepsTheCardRunningWithTheSameUrl()
    {
        var (service, broker) = Service();
        using var _s = service;
        using var a = broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        ServerPreviewStart? sent = null;
        broker.PreviewCommanded += c => sent ??= c.Start;
        var board = Guid.NewGuid();
        var repo = new WorkspaceRepoEntity { Id = Guid.NewGuid(), Owner = "octo", Repo = "app", DefaultBranch = "main" };
        await service.StartAsync("t1", "u1", board, repo, Recipe());
        sent!.IdleMinutes.ShouldBe(AutopilotSettings.DefaultPreviewIdleMinutes);
        sent.BoardId.ShouldBe(board);
        sent.RepoId.ShouldBe(repo.Id);
        broker.ReportPreview("conn-a", sent.PreviewId, PreviewStates.Ready, "http://localhost:3000/");

        a.Dispose(); // runner restart
        service.Get("t1", repo.Id)!.IsActive.ShouldBeTrue();
        service.IsActiveOnBoard("t1", board).ShouldBeTrue();
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        // Whatever url the runner sends now, the card keeps the one the Board started it with.
        broker.AnnouncePreviews("conn-b", [Announced(sent.PreviewId) with { BoardId = board, RepoId = repo.Id, Url = "https://evil.example/" }], []);

        var state = service.Get("t1", repo.Id)!;
        state.Id.ShouldBe(sent.PreviewId);
        state.State.ShouldBe(PreviewStates.Ready);
        state.Url.ShouldBe("http://localhost:3000/");
        state.Message.ShouldBe("http://localhost:3000/");
        service.Stop("t1", repo.Id);
        broker.ReportPreview("conn-b", sent.PreviewId, PreviewStates.Stopped, null);
        service.Get("t1", repo.Id)!.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void Reattach_UnknownPreview_RebuildsTheCard_AfterAWebRestart()
    {
        var (service, broker) = Service(StoredRepo());
        using var _s = service;
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var changed = 0;
        service.Changed += _ => changed++;
        var pushed = new List<PreviewCommand>();
        broker.PreviewCommanded += pushed.Add;

        broker.AnnouncePreviews("conn-b", [Announced("p9")], []);

        var state = service.Get("t1", RepoA).ShouldNotBeNull();
        state.Id.ShouldBe("p9");
        state.UserId.ShouldBe("u1");
        state.BoardId.ShouldBe(BoardA);
        state.Repo.ShouldBe("octo/app");
        state.Url.ShouldBe("http://localhost:3000/");
        state.IsActive.ShouldBeTrue();
        changed.ShouldBe(1);
        pushed.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("external url")]
    [InlineData("javascript url")]
    [InlineData("data url")]
    [InlineData("repo of another board")]
    [InlineData("repo of another tenant")]
    [InlineData("mismatched owner/repo")]
    [InlineData("repo the store doesn't have")]
    [InlineData("no board")]
    public void Reattach_UnknownPreview_TheBoardCantVouchFor_IsRefusedAndStopped(string why)
    {
        var (announced, stored) = why switch
        {
            "external url" => (Announced("p9") with { Url = "https://evil.example/login" }, new[] { StoredRepo() }),
            "javascript url" => (Announced("p9") with { Url = "javascript:alert(document.cookie)" }, [StoredRepo()]),
            "data url" => (Announced("p9") with { Url = "data:text/html,<script>alert(1)</script>" }, [StoredRepo()]),
            "repo of another board" => (Announced("p9"), [StoredRepo(board: Guid.NewGuid())]),
            "repo of another tenant" => (Announced("p9"), [StoredRepo(tenant: "t2")]),
            "mismatched owner/repo" => (Announced("p9") with { Owner = "someone", Repo = "else" }, [StoredRepo()]),
            "repo the store doesn't have" => (Announced("p9"), []),
            "no board" => (Announced("p9") with { BoardId = Guid.Empty }, [StoredRepo()]),
            _ => throw new ArgumentOutOfRangeException(nameof(why)),
        };
        var (service, broker) = Service(stored);
        using var _s = service;
        using var b = broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "t1", "u1"));
        var pushed = new List<PreviewCommand>();
        broker.PreviewCommanded += pushed.Add;

        broker.AnnouncePreviews("conn-b", [announced], []);

        service.Get("t1", announced.RepoId).ShouldBeNull();
        service.IsActiveOnBoard("t1", BoardA).ShouldBeFalse();
        pushed.ShouldHaveSingleItem().ShouldBe(new PreviewCommand("conn-b", null, "p9"));
    }

    [Theory]
    [InlineData("http://localhost:3000/", true)]
    [InlineData("https://app.localhost:5001/", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("https://evil.example/", false)]
    [InlineData("http://localhost.evil.example/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalHttpUrl_OnlyHttpOnThisComputer_IsPutBehindOpenTheApp(string? url, bool expected) =>
        PreviewRecipe.IsLocalHttpUrl(url).ShouldBe(expected);

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

    // The tenant's workspace store, as far as the Board's re-attach check reads it: a board's repos, per tenant.
    private sealed class FakeWorkspaces(IReadOnlyList<WorkspaceRepoEntity> repos) : IWorkspaceRepository
    {
        public Task<IReadOnlyList<WorkspaceRepoEntity>> ListReposForTenantAsync(string tenantId, Guid workspaceId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkspaceRepoEntity>>([.. repos.Where(r => r.TenantId == tenantId && r.WorkspaceId == workspaceId)]);

        public Task<IReadOnlyList<WorkspaceEntity>> ListAsync(int limit = 50, int offset = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkspaceEntity?> GetAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(WorkspaceEntity workspace, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkspaceEntity>> ListForTenantAsync(string tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkspaceEntity?> GetForTenantAsync(string tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveForTenantAsync(string tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddForTenantAsync(WorkspaceEntity workspace, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetAutopilotForTenantAsync(string tenantId, Guid id, string mode, string? userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAutopilotEventAsync(string tenantId, Guid boardId, string text, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AutopilotEventEntity>> ListAutopilotEventsAsync(string tenantId, Guid boardId, int max, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkspaceEntity>> ListAutopilotAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkspaceRepoEntity>> ListAllReposForTenantAsync(string tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddRepoForTenantAsync(WorkspaceRepoEntity repo, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveRepoForTenantAsync(string tenantId, Guid repoId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
