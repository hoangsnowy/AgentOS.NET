// A run's background children (a dev server it started and left behind) must die with the run — on the lab a vite
// server from a finished run held port 5173 and made "Try the app" report a stranger's server as ready.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public class ProcessJobTests
{
    [Fact]
    public async Task Dispose_KillsABackgroundChildThatOutlivedItsParent()
    {
        if (!OperatingSystem.IsWindows()) { return; } // the Job Object guarantee is Windows-only (see ProcessJob.cs)

        var pidFile = Path.Join(Path.GetTempPath(), $"agentos-job-{Guid.NewGuid():N}.txt");
        try
        {
            // The parent starts a detached child, writes its pid, and exits at once — the child is now an orphan.
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"$p = Start-Process ping -ArgumentList '-n','120','127.0.0.1' -PassThru -WindowStyle Hidden; Set-Content -Path '{pidFile}' -Value $p.Id");

            using var job = ProcessJob.Create();
            using (var parent = Process.Start(psi)!)
            {
                job.Add(parent);
                await parent.WaitForExitAsync();
            }
            var childPid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            using var child = Process.GetProcessById(childPid);
            child.HasExited.ShouldBeFalse();

            job.Dispose();

            (await Task.Run(() => child.WaitForExit(TimeSpan.FromSeconds(10)))).ShouldBeTrue();
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    // The cleanup works from the run's processes: a background child is a member (with its start time), is remembered
    // once sampled, and can be killed on its own.
    [Fact]
    public async Task Members_BackgroundChild_IsSampledAndKillableOnItsOwn()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }
            : new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("$p = Start-Process ping -ArgumentList '-n','120','127.0.0.1' -PassThru -WindowStyle Hidden; Write-Output $p.Id; Start-Sleep 60");
        }
        else
        {
            psi.ArgumentList.Add("-c");
            // `wait` reaps the child once it is killed (a killed child its parent never reaps stays a zombie, which still
            // "runs" by pid); the shell itself then keeps running.
            psi.ArgumentList.Add("sleep 120 & echo $!; wait; sleep 60");
        }

        using var job = ProcessJob.Create();
        using var parent = Process.Start(psi)!;
        job.Add(parent);
        var childPid = int.Parse((await parent.StandardOutput.ReadLineAsync())!.Trim(), System.Globalization.CultureInfo.InvariantCulture);

        job.Sample();
        var child = job.Members().Where(p => p.Pid == childPid).ShouldHaveSingleItem();
        job.Seen.ShouldContain(child);
        job.IsAlive(child).ShouldBeTrue();
        using var handle = Process.GetProcessById(childPid);

        job.Kill(child);

        (await Task.Run(() => handle.WaitForExit(TimeSpan.FromSeconds(10)))).ShouldBeTrue();
        job.IsAlive(child).ShouldBeFalse();
        job.IsAlive(job.Members().Single(p => p.Pid == parent.Id)).ShouldBeTrue(); // only that one process
    }

    [Fact]
    public void IsAlive_SamePidOtherStartTime_IsAnotherProcess()
    {
        using var job = ProcessJob.Create();
        using var self = Process.GetCurrentProcess();
        var me = new RunProcess(self.Id, self.StartTime.ToUniversalTime(), self.ProcessName);

        job.IsAlive(me).ShouldBeTrue();
        job.IsAlive(me with { StartedUtc = me.StartedUtc.AddMinutes(-5) }).ShouldBeFalse(); // a pid reused since
    }

    [Fact]
    public void DescendantsOf_PsOutput_FollowsTheTreeOnly()
    {
        const string ps = """
              1     0
            100     1
            101   100
            102   101
            200     1
            201   200
            """;

        ProcessJob.DescendantsOf([100], ps).ShouldBe([100, 101, 102]);
    }

    [Fact]
    public void Dispose_Twice_IsHarmless()
    {
        var job = ProcessJob.Create();
        job.Dispose();
        Should.NotThrow(job.Dispose);
    }
}
