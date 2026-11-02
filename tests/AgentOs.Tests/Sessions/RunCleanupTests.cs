// A run leaves nothing behind: a Testing agent's Aspire AppHost left Postgres, Keycloak, MailHog, a DCP tunnel proxy, its
// session network and its data volume behind on the lab. The end-of-run cleanup lets DCP remove what it created, kills
// what's left, and removes only the containers / networks whose DCP creator was one of THIS run's processes — never the
// lab stack's or a "Try the app" preview's (their creator lives outside the run's job) — and the named volumes those
// containers mounted that were created during the run. Docker and the run's processes are fakes here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class RunCleanupTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 15, 52, 20, 121, DateTimeKind.Utc);
    private static readonly DateTime RunStart = T0.AddMinutes(-2);

    // The run: a shell, the AppHost it started in the background, and the AppHost's DCP (api server + controllers).
    private static readonly RunProcess Shell = new(100, T0.AddSeconds(-30), "bash");
    private static readonly RunProcess AppHost = new(200, T0.AddSeconds(-5), "ThuVienCo.AppHost");
    private static readonly RunProcess DcpApi = new(300, T0.AddSeconds(-1), "dcp");
    private static readonly RunProcess DcpControllers = new(301, T0, "dcp");

    // Outside the run: the lab stack's DCP and a preview's, both alive.
    private const int LabDcpPid = 35064;
    private static readonly DateTime LabDcpStarted = new(2026, 11, 2, 13, 46, 5, 797, DateTimeKind.Utc);
    private const int PreviewDcpPid = 41000;
    private static readonly DateTime PreviewDcpStarted = new(2026, 11, 2, 14, 10, 0, 0, DateTimeKind.Utc);

    private static string Line(string id, string name, int pid, DateTime startedUtc) =>
        $"{id}|{name}|{pid}|{new DateTimeOffset(startedUtc).ToOffset(TimeSpan.FromHours(7)):yyyy-MM-ddTHH:mm:ss.fffzzz}";

    private static readonly string Listing = string.Join('\n',
        Line("aa01", "postgres-jrwbbahn", DcpControllers.Pid, DcpControllers.StartedUtc),
        Line("aa02", "keycloak-rjrytsmd", DcpControllers.Pid, DcpControllers.StartedUtc),
        Line("aa03", "mailhog-cynmrhdp", DcpControllers.Pid, DcpControllers.StartedUtc),
        Line("aa04", "aspire-container-network-tunnelproxy-c2djbs6j14", DcpControllers.Pid, DcpControllers.StartedUtc),
        Line("bb01", "postgres-zspnuhkk", LabDcpPid, LabDcpStarted),                        // the lab stack
        Line("bb02", "aspire-container-network-tunnelproxy-eemiub1j8c", LabDcpPid, LabDcpStarted),
        Line("cc01", "postgres-preview1", PreviewDcpPid, PreviewDcpStarted),                // a "Try the app" preview
        Line("dd01", "postgres-oldrun", DcpControllers.Pid, DcpControllers.StartedUtc.AddHours(-30))); // a reused pid

    private static readonly string Networks = string.Join('\n',
        Line("ee01", "aspire-session-network-hcezjxrc-ThuVienCo", DcpControllers.Pid, DcpControllers.StartedUtc),
        Line("ee02", "aspire-session-network-tubqmwfw-AgentOs", LabDcpPid, LabDcpStarted),  // the lab stack's
        Line("ee03", "aspire-session-network-rtdywpcd-ThuVienCo", PreviewDcpPid, PreviewDcpStarted));

    // `docker inspect` of the run's containers: postgres mounts its data volume (+ an anonymous one), keycloak a volume
    // that existed before the run (the project's shared data), the others nothing.
    private const string Mounts =
        "thuvienco-postgres-data-13416a39 0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0 \nthuvienco-keycloak-data \n\n\n";

    private static string VolumeInspect() => string.Join('\n',
        $"thuvienco-postgres-data-13416a39|{RunStart.AddSeconds(90):yyyy-MM-ddTHH:mm:ssZ}",
        $"thuvienco-keycloak-data|{RunStart.AddDays(-3):yyyy-MM-ddTHH:mm:ssZ}");

    private static FakeDocker Docker(string containers) => new(new Dictionary<string, DockerResult>
    {
        ["ps"] = new(0, containers, string.Empty),
        ["network ls"] = new(0, Networks, string.Empty),
        ["inspect"] = new(0, Mounts, string.Empty),
        ["volume inspect"] = new(0, VolumeInspect(), string.Empty),
    });

    private static Task<CleanupOutcome> End(FakeTree tree, FakeDocker docker, CleanupOptions options, Action<string>? log = null) =>
        RunCleanup.EndAsync(tree, docker, options, log ?? (_ => { }), RunStart);

    [Fact]
    public async Task EndAsync_DcpStopsItself_AppHostKilledFirstAndNothingLeftToRemove()
    {
        var tree = new FakeTree([Shell, AppHost, DcpApi, DcpControllers]) { DcpStopsWhenAppHostDies = true };
        var docker = Docker(string.Empty);
        var log = new List<string>();

        var outcome = await End(tree, docker, new CleanupOptions(false, TimeSpan.FromSeconds(5)), log.Add);

        tree.KilledBeforeDispose.ShouldBe([Shell, AppHost], ignoreOrder: true); // never DCP itself: it cleans up
        tree.Disposed.ShouldBeTrue();
        outcome.OrchestratorStoppedItself.ShouldBeTrue();
        outcome.ContainersRemoved.ShouldBeEmpty();
        docker.Calls.ShouldNotContain(c => c[0] == "rm");
        log.ShouldContain(l => l.Contains("stopped its containers itself", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndAsync_DcpDoesNotStop_ForcesAndRemovesOnlyTheRunsContainersNetworksAndNewVolumes()
    {
        var tree = new FakeTree([Shell, AppHost, DcpApi, DcpControllers]) { DcpStopsWhenAppHostDies = false };
        var docker = Docker(Listing);
        var log = new List<string>();

        var outcome = await End(tree, docker, new CleanupOptions(false, TimeSpan.FromMilliseconds(300)), log.Add);

        outcome.OrchestratorStoppedItself.ShouldBeFalse();
        tree.Disposed.ShouldBeTrue();
        // Containers: the run's three + its tunnel proxy, with their anonymous volumes (-v never removes a named one).
        var rm = docker.Calls.Single(c => c[0] == "rm");
        rm.Take(3).ShouldBe(["rm", "-f", "-v"]);
        rm.Skip(3).ShouldBe(["aa01", "aa02", "aa03", "aa04"]);
        docker.Calls.Single(c => c[0] == "inspect").Skip(3).ShouldBe(["aa01", "aa02", "aa03", "aa04"]); // mounts asked before rm
        outcome.ContainersRemoved.ShouldBe(["postgres-jrwbbahn", "keycloak-rjrytsmd", "mailhog-cynmrhdp", "aspire-container-network-tunnelproxy-c2djbs6j14"]);
        // Networks: only the run's session network — never the lab's or the preview's.
        docker.Calls.Single(c => c is ["network", "rm", ..]).Skip(2).ShouldBe(["ee01"]);
        outcome.NetworksRemoved.ShouldBe(["aspire-session-network-hcezjxrc-ThuVienCo"]);
        // Volumes: the named one created during the run; the older shared one stays; the anonymous one went with -v.
        docker.Calls.Single(c => c is ["volume", "inspect", ..]).ShouldBe(
            ["volume", "inspect", "--format", "{{.Name}}|{{.CreatedAt}}", "thuvienco-postgres-data-13416a39", "thuvienco-keycloak-data"]);
        docker.Calls.Single(c => c is ["volume", "rm", ..]).Skip(2).ShouldBe(["thuvienco-postgres-data-13416a39"]);
        outcome.VolumesRemoved.ShouldBe(["thuvienco-postgres-data-13416a39"]);
        log.ShouldContain(l => l.Contains("forcing", StringComparison.Ordinal));
        log.ShouldContain(l => l.Contains("removed 4 container(s)", StringComparison.Ordinal));
        log.ShouldContain(l => l.Contains("removed 1 network(s)", StringComparison.Ordinal));
        log.ShouldContain(l => l.Contains("removed 1 volume(s)", StringComparison.Ordinal));
    }

    // Stop on the Board kills the CLI's whole tree at once — DCP included — before the cleanup runs: the containers are
    // still found, because the run's processes were sampled while they lived.
    [Fact]
    public async Task EndAsync_RunStoppedAndDcpAlreadyGone_SampledCreatorStillAttributesItsContainers()
    {
        var tree = new FakeTree([Shell, AppHost, DcpApi, DcpControllers]);
        tree.Sample();
        tree.KillAllNow();
        var docker = Docker(Listing);

        var outcome = await End(tree, docker, new CleanupOptions(false, TimeSpan.FromSeconds(5)));

        outcome.ContainersRemoved.Count.ShouldBe(4);
        outcome.NetworksRemoved.ShouldBe(["aspire-session-network-hcezjxrc-ThuVienCo"]);
        docker.Calls.Single(c => c[0] == "rm").Skip(3).ShouldBe(["aa01", "aa02", "aa03", "aa04"]);
    }

    [Fact]
    public async Task EndAsync_NoOrchestratorInTheRun_NeverAsksDocker()
    {
        var tree = new FakeTree([Shell, new RunProcess(500, T0, "node")]);
        var docker = Docker(Listing);

        var outcome = await End(tree, docker, new CleanupOptions(false, TimeSpan.FromSeconds(5)));

        docker.Calls.ShouldBeEmpty();
        tree.KilledBeforeDispose.ShouldBeEmpty(); // no graceful step: the job closes them
        tree.Disposed.ShouldBeTrue();
        outcome.ProcessesLeft.ShouldBe(2);
    }

    [Fact]
    public async Task EndAsync_KeepContainers_NoGracefulStopAndNoRemoval()
    {
        var tree = new FakeTree([Shell, AppHost, DcpApi, DcpControllers]);
        var docker = Docker(Listing);

        await End(tree, docker, CleanupOptions.FromSetting("1"));

        docker.Calls.ShouldBeEmpty();
        tree.KilledBeforeDispose.ShouldBeEmpty();
        tree.Disposed.ShouldBeTrue(); // processes still end
    }

    [Fact]
    public async Task EndAsync_KeepVolumes_ContainersAndNetworksGoVolumesStay()
    {
        var tree = new FakeTree([Shell, AppHost, DcpApi, DcpControllers]) { DcpStopsWhenAppHostDies = false };
        var docker = Docker(Listing);

        var outcome = await End(tree, docker, CleanupOptions.FromSetting(null, "1") with { GracefulWait = TimeSpan.Zero });

        docker.Calls.Single(c => c[0] == "rm").Take(3).ShouldBe(["rm", "-f", "aa01"]); // no -v
        docker.Calls.ShouldNotContain(c => c[0] == "inspect" || c[0] == "volume");
        outcome.NetworksRemoved.Count.ShouldBe(1);
        outcome.VolumesRemoved.ShouldBeEmpty();
    }

    [Fact]
    public async Task EndAsync_DockerMissing_LogsAndRemovesNothing()
    {
        var tree = new FakeTree([DcpControllers]) { DcpStopsWhenAppHostDies = false };
        var docker = new FakeDocker(new Dictionary<string, DockerResult>(), new DockerResult(-1, string.Empty, "The system cannot find the file specified."));
        var log = new List<string>();

        var outcome = await End(tree, docker, new CleanupOptions(false, TimeSpan.Zero), log.Add);

        outcome.ContainersRemoved.ShouldBeEmpty();
        outcome.NetworksRemoved.ShouldBeEmpty();
        docker.Calls.ShouldNotContain(c => c[0] == "rm" || (c.Count > 1 && c[1] == "rm"));
        log.ShouldContain(l => l.StartsWith("couldn't list the app's containers", StringComparison.Ordinal));
    }

    [Fact]
    public void Attributed_OnlyCreatorsOfTheRun_NeverLabPreviewOrReusedPid()
    {
        var chosen = RunCleanup.Attributed(RunCleanup.Parse(Listing), [Shell, AppHost, DcpApi, DcpControllers]);

        chosen.Select(c => c.Id).ShouldBe(["aa01", "aa02", "aa03", "aa04"]);
    }

    [Fact]
    public void Attributed_TheLabsOwnProcesses_NeverChooseARunsContainers()
    {
        // The other way round: a cleanup that only knew the lab's DCP would pick the lab's containers — the rule is the
        // creator, so a run whose job never held the lab's DCP can't.
        var runOnly = RunCleanup.Attributed(RunCleanup.Parse(Listing), [Shell, AppHost]);

        runOnly.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not-a-line")]
    [InlineData("zz!!|name|1|2026-11-02T22:52:20.121+07:00")]
    [InlineData("aa01|name|notapid|2026-11-02T22:52:20.121+07:00")]
    [InlineData("aa01|name|12|yesterday")]
    [InlineData("aa01|name|12")]
    public void Parse_MalformedLine_IsSkipped(string line) => RunCleanup.Parse(line).ShouldBeEmpty();

    [Fact]
    public void Parse_DcpLabels_ReadsCreatorPidAndStartInUtc()
    {
        var c = RunCleanup.Parse("01b816c62ace|postgres-jrwbbahn|28004|2026-11-02T22:52:20.121+07:00").ShouldHaveSingleItem();

        c.CreatorPid.ShouldBe(28004);
        c.CreatorStartedUtc.ShouldBe(new DateTime(2026, 11, 2, 15, 52, 20, 121, DateTimeKind.Utc));
        c.Name.ShouldBe("postgres-jrwbbahn");
    }

    [Fact]
    public void ListArgs_FilterOnTheDcpCreatorLabel()
    {
        RunCleanup.ListArgs().ShouldContain("label=com.microsoft.developer.usvc-dev.creatorProcessId");
        RunCleanup.NetworkListArgs().Take(2).ShouldBe(["network", "ls"]);
        RunCleanup.NetworkListArgs().ShouldContain("label=com.microsoft.developer.usvc-dev.creatorProcessId");
    }

    [Fact]
    public void NamedVolumes_SkipsAnonymousOnesAndJunk() =>
        RunCleanup.NamedVolumes(Mounts + " -rf ").ShouldBe(["thuvienco-postgres-data-13416a39", "thuvienco-keycloak-data"]);

    [Fact]
    public void VolumesCreatedSince_OnlyThoseCreatedDuringTheRun() =>
        RunCleanup.VolumesCreatedSince(VolumeInspect() + "\nbroken line", RunStart).ShouldBe(["thuvienco-postgres-data-13416a39"]);

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("", "0", false, false)]
    [InlineData("1", null, true, true)]   // keeping containers keeps their volumes
    [InlineData(" TRUE ", null, true, true)]
    [InlineData(null, "on", false, true)]
    public void CleanupOptions_FromSetting_KeepsOnlyWhenAsked(string? containers, string? volumes, bool keepContainers, bool keepVolumes)
    {
        var options = CleanupOptions.FromSetting(containers, volumes);

        options.KeepContainers.ShouldBe(keepContainers);
        options.KeepVolumes.ShouldBe(keepVolumes);
    }

    [Fact]
    public void Safe_ControlCharacters_CannotForgeALogLine() =>
        RunCleanup.Safe("evil\n[agent] forged\r\x1b[2J").ShouldNotContain('\n');

    [Fact]
    public void RunProcess_Is_SamePidAndStartWithinASecond()
    {
        DcpControllers.Is(301, T0.AddMilliseconds(400)).ShouldBeTrue();
        DcpControllers.Is(301, T0.AddSeconds(5)).ShouldBeFalse();
        DcpControllers.Is(302, T0).ShouldBeFalse();
    }

    // ── fakes ──────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeTree(IReadOnlyList<RunProcess> processes) : IRunProcessTree
    {
        private readonly HashSet<RunProcess> _alive = [.. processes];
        private readonly HashSet<RunProcess> _seen = [];

        public bool DcpStopsWhenAppHostDies { get; init; } = true;
        public List<RunProcess> KilledBeforeDispose { get; } = [];
        public bool Disposed { get; private set; }
        public IReadOnlyCollection<RunProcess> Seen => [.. _seen];

        public void Sample() => _seen.UnionWith(_alive);
        public IReadOnlyList<RunProcess> Members() => [.. _alive];
        public bool IsAlive(RunProcess process) => _alive.Contains(process);

        public void Kill(RunProcess process)
        {
            if (!_alive.Remove(process)) { return; }
            KilledBeforeDispose.Add(process);
            if (DcpStopsWhenAppHostDies && process == AppHost) { _alive.RemoveWhere(RunCleanup.IsOrchestrator); }
        }

        public void KillAllNow() => _alive.Clear();

        public void Dispose()
        {
            _alive.Clear();
            Disposed = true;
        }
    }

    // A fake docker CLI: answers by command ("ps", "network ls", "inspect", "volume inspect"); removals succeed.
    private sealed class FakeDocker(IReadOnlyDictionary<string, DockerResult> answers, DockerResult? fallback = null) : IDockerCli
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<DockerResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Calls.Add([.. args]);
            var two = args.Count > 1 ? args[0] + " " + args[1] : args[0];
            if (answers.TryGetValue(two, out var byTwo)) { return Task.FromResult(byTwo); }
            if (answers.TryGetValue(args[0], out var byOne)) { return Task.FromResult(byOne); }
            return Task.FromResult(fallback ?? new DockerResult(0, string.Empty, string.Empty));
        }
    }
}

public sealed class RunsRootWatchTests
{
    private static string NewRoot()
    {
        var root = Path.Join(Path.GetTempPath(), "agentos-runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void End_StraysCreatedDuringTheRun_RemovedRunFoldersAndMarkersKept()
    {
        var root = NewRoot();
        try
        {
            var watch = new RunsRootWatch();
            watch.Begin(DateTime.UtcNow.AddSeconds(-10));
            var run = Path.Join(root, "20261102-154906-d1ed26ddc4d74ab186c0ce6d6559fba7");
            var other = Path.Join(root, "20261102-155000-files-0f0e");
            Directory.CreateDirectory(run);
            Directory.CreateDirectory(other);
            File.WriteAllText(run + RunContinuation.MarkerSuffix, string.Empty);
            var stray = Path.Join(root, "base-122");
            Directory.CreateDirectory(Path.Join(stray, "web", "src"));
            File.WriteAllText(Path.Join(stray, "web", "src", "index.css"), "x");
            File.WriteAllText(Path.Join(root, "validate-out.json"), "{}");
            var log = new List<string>();

            var found = watch.End(root, remove: true, log.Add);

            found.ShouldBe(["base-122", "validate-out.json"], ignoreOrder: true);
            Directory.Exists(stray).ShouldBeFalse();
            File.Exists(Path.Join(root, "validate-out.json")).ShouldBeFalse();
            Directory.Exists(run).ShouldBeTrue();
            Directory.Exists(other).ShouldBeTrue();
            File.Exists(run + RunContinuation.MarkerSuffix).ShouldBeTrue();
            log.ShouldContain(l => l.Contains("base-122", StringComparison.Ordinal));
        }
        finally
        {
            _ = TestFiles.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void End_EntryOlderThanTheRuns_IsLeftAlone()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Join(root, "trx84")); // there before the run: not attributable to it
            var watch = new RunsRootWatch();
            watch.Begin(DateTime.UtcNow.AddSeconds(30));

            watch.End(root, remove: true, _ => { }).ShouldBeEmpty();

            Directory.Exists(Path.Join(root, "trx84")).ShouldBeTrue();
        }
        finally
        {
            _ = TestFiles.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void End_AnotherRunStillInFlight_WaitsForTheLastOne()
    {
        var root = NewRoot();
        try
        {
            var watch = new RunsRootWatch();
            watch.Begin(DateTime.UtcNow.AddSeconds(-10));
            watch.Begin(DateTime.UtcNow);
            var stray = Path.Join(root, "base-123");
            Directory.CreateDirectory(stray); // the other run may still be using it

            watch.End(root, remove: true, _ => { }).ShouldBeEmpty();
            Directory.Exists(stray).ShouldBeTrue();

            watch.End(root, remove: true, _ => { }).ShouldBe(["base-123"]);
            Directory.Exists(stray).ShouldBeFalse();
        }
        finally
        {
            _ = TestFiles.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void End_CustomWorkRoot_OnlyReports()
    {
        var root = NewRoot();
        try
        {
            var watch = new RunsRootWatch();
            watch.Begin(DateTime.UtcNow.AddSeconds(-10));
            var stray = Path.Join(root, "notes");
            Directory.CreateDirectory(stray);
            var log = new List<string>();

            watch.End(root, remove: false, log.Add).ShouldBe(["notes"]);

            Directory.Exists(stray).ShouldBeTrue();
            log.ShouldHaveSingleItem().ShouldContain("not removed");
        }
        finally
        {
            _ = TestFiles.TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("20261102-154906-d1ed26ddc4d74ab186c0ce6d6559fba7", true)]
    [InlineData("20261102-154906-d1ed26ddc4d74ab186c0ce6d6559fba7.parked", true)]
    [InlineData("20261102-154906-d1ed26ddc4d74ab186c0ce6d6559fba7.tmp", true)]
    [InlineData("base-122.tmp", false)]
    [InlineData("base-122", false)]
    [InlineData("trx84", false)]
    [InlineData("20261102-154906-", false)]
    [InlineData("20261102-154906-x.json", false)]
    public void IsRunnersOwn_OnlyTheRunnersNames(string name, bool own) => RunsRootWatch.IsRunnersOwn(name).ShouldBe(own);

    [Fact]
    public void SweepTempLeftovers_RemovesRunTempFoldersOnly()
    {
        var root = NewRoot();
        try
        {
            var run = Path.Join(root, "20261102-154906-d1ed26ddc4d74ab186c0ce6d6559fba7");
            Directory.CreateDirectory(run);
            Directory.CreateDirectory(run + RunsRootWatch.TempSuffix);
            Directory.CreateDirectory(Path.Join(root, "notes.tmp"));

            RunsRootWatch.SweepTempLeftovers(root).ShouldBe(1);

            Directory.Exists(run + RunsRootWatch.TempSuffix).ShouldBeFalse();
            Directory.Exists(run).ShouldBeTrue();
            Directory.Exists(Path.Join(root, "notes.tmp")).ShouldBeTrue();
        }
        finally
        {
            _ = TestFiles.TryDeleteDirectory(root);
        }
    }
}
