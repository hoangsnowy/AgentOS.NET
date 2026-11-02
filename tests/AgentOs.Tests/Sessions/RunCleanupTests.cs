// A run leaves nothing behind: a Testing agent's Aspire AppHost left Postgres, Keycloak, MailHog and a DCP tunnel proxy
// running after the run on the lab. The end-of-run cleanup lets DCP remove its containers, kills what's left, and
// removes only the containers whose DCP creator was one of THIS run's processes — never the lab stack's or a "Try the
// app" preview's (their creator lives outside the run's job). Docker and the run's processes are fakes here.

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

    [Fact]
    public async Task EndAsync_DcpStopsItself_AppHostKilledFirstAndNothingLeftToRemove()
    {
        var tree = new FakeTree([Shell, AppHost, DcpApi, DcpControllers]) { DcpStopsWhenAppHostDies = true };
        var docker = new FakeDocker(string.Empty);
        var log = new List<string>();

        var outcome = await RunCleanup.EndAsync(tree, docker, new CleanupOptions(false, TimeSpan.FromSeconds(5)), log.Add);

        tree.KilledBeforeDispose.ShouldBe([Shell, AppHost], ignoreOrder: true); // never DCP itself: it cleans up
        tree.Disposed.ShouldBeTrue();
        outcome.OrchestratorStoppedItself.ShouldBeTrue();
        outcome.ContainersRemoved.ShouldBeEmpty();
        docker.Calls.ShouldHaveSingleItem()[0].ShouldBe("ps"); // listed (nothing of the run's left), nothing removed
        log.ShouldContain(l => l.Contains("stopped its containers itself", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndAsync_DcpDoesNotStop_ForcesAndRemovesOnlyTheRunsContainers()
    {
        var tree = new FakeTree([Shell, AppHost, DcpApi, DcpControllers]) { DcpStopsWhenAppHostDies = false };
        var docker = new FakeDocker(Listing);
        var log = new List<string>();

        var outcome = await RunCleanup.EndAsync(tree, docker, new CleanupOptions(false, TimeSpan.FromMilliseconds(300)), log.Add);

        outcome.OrchestratorStoppedItself.ShouldBeFalse();
        tree.Disposed.ShouldBeTrue();
        docker.Calls.Count.ShouldBe(2);
        var rm = docker.Calls[1];
        rm.Take(2).ShouldBe(["rm", "-f"]);
        rm.Skip(2).ShouldBe(["aa01", "aa02", "aa03", "aa04"]);         // the run's three containers + its tunnel proxy
        rm.ShouldNotContain("-v");                                       // named volumes are the project's data
        rm.ShouldNotContain("--volumes");
        outcome.ContainersRemoved.ShouldBe(["postgres-jrwbbahn", "keycloak-rjrytsmd", "mailhog-cynmrhdp", "aspire-container-network-tunnelproxy-c2djbs6j14"]);
        log.ShouldContain(l => l.Contains("forcing", StringComparison.Ordinal));
        log.ShouldContain(l => l.Contains("removed 4 container(s)", StringComparison.Ordinal) && l.Contains("volumes kept", StringComparison.Ordinal));
    }

    // Stop on the Board kills the CLI's whole tree at once — DCP included — before the cleanup runs: the containers are
    // still found, because the run's processes were sampled while they lived.
    [Fact]
    public async Task EndAsync_RunStoppedAndDcpAlreadyGone_SampledCreatorStillAttributesItsContainers()
    {
        var tree = new FakeTree([Shell, AppHost, DcpApi, DcpControllers]);
        tree.Sample();
        tree.KillAllNow();
        var docker = new FakeDocker(Listing);

        var outcome = await RunCleanup.EndAsync(tree, docker, new CleanupOptions(false, TimeSpan.FromSeconds(5)), _ => { });

        outcome.ContainersRemoved.Count.ShouldBe(4);
        docker.Calls[1].Skip(2).ShouldBe(["aa01", "aa02", "aa03", "aa04"]);
    }

    [Fact]
    public async Task EndAsync_NoOrchestratorInTheRun_NeverAsksDocker()
    {
        var tree = new FakeTree([Shell, new RunProcess(500, T0, "node")]);
        var docker = new FakeDocker(Listing);

        var outcome = await RunCleanup.EndAsync(tree, docker, new CleanupOptions(false, TimeSpan.FromSeconds(5)), _ => { });

        docker.Calls.ShouldBeEmpty();
        tree.KilledBeforeDispose.ShouldBeEmpty(); // no graceful step: the job closes them
        tree.Disposed.ShouldBeTrue();
        outcome.ProcessesLeft.ShouldBe(2);
    }

    [Fact]
    public async Task EndAsync_KeepContainers_NoGracefulStopAndNoRemoval()
    {
        var tree = new FakeTree([Shell, AppHost, DcpApi, DcpControllers]);
        var docker = new FakeDocker(Listing);

        await RunCleanup.EndAsync(tree, docker, CleanupOptions.FromSetting("1"), _ => { });

        docker.Calls.ShouldBeEmpty();
        tree.KilledBeforeDispose.ShouldBeEmpty();
        tree.Disposed.ShouldBeTrue(); // processes still end
    }

    [Fact]
    public async Task EndAsync_DockerMissing_LogsAndRemovesNothing()
    {
        var tree = new FakeTree([DcpControllers]) { DcpStopsWhenAppHostDies = false };
        var docker = new FakeDocker(string.Empty, exitCode: -1, error: "The system cannot find the file specified.");
        var log = new List<string>();

        var outcome = await RunCleanup.EndAsync(tree, docker, new CleanupOptions(false, TimeSpan.Zero), log.Add);

        outcome.ContainersRemoved.ShouldBeEmpty();
        docker.Calls.ShouldHaveSingleItem();
        log.ShouldContain(l => l.StartsWith("couldn't list", StringComparison.Ordinal));
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
    public void ListArgs_FiltersOnTheDcpCreatorLabel() =>
        RunCleanup.ListArgs().ShouldContain("label=com.microsoft.developer.usvc-dev.creatorProcessId");

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("on", true)]
    public void CleanupOptions_FromSetting_KeepsOnlyWhenAsked(string? setting, bool keep) =>
        CleanupOptions.FromSetting(setting).KeepContainers.ShouldBe(keep);

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

    private sealed class FakeDocker(string listing, int exitCode = 0, string error = "") : IDockerCli
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<DockerResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Calls.Add([.. args]);
            return Task.FromResult(args[0] == "ps" ? new DockerResult(exitCode, listing, error) : new DockerResult(0, string.Empty, string.Empty));
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
    [InlineData("base-122", false)]
    [InlineData("trx84", false)]
    [InlineData("20261102-154906-", false)]
    [InlineData("20261102-154906-x.json", false)]
    public void IsRunnersOwn_OnlyTheRunnersNames(string name, bool own) => RunsRootWatch.IsRunnersOwn(name).ShouldBe(own);
}
