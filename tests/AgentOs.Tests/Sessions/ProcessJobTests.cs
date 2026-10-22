// A run's background children (a dev server it started and left behind) must die with the run — on the lab a vite
// server from a finished run held port 5173 and made "Try the app" report a stranger's server as ready.

using System;
using System.Diagnostics;
using System.IO;
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

    [Fact]
    public void Dispose_Twice_IsHarmless()
    {
        var job = ProcessJob.Create();
        job.Dispose();
        Should.NotThrow(job.Dispose);
    }
}
