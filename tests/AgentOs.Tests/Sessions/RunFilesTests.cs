// Ticket files (ADR-0015), runner side — what the paired computer writes into a run directory before the CLI starts.
// Real temp folders: the files land at <run dir>/.agentos/files/<path>, nothing is ever written outside it (a crafted
// path from a compromised server is refused), the bytes must match the announced size + SHA-256, a refused file is
// removed, and an existing file is never overwritten.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class RunFilesTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "agentos-runfiles-" + Guid.NewGuid().ToString("N"));
    private readonly string _runDir;

    public RunFilesTests()
    {
        _runDir = Path.Join(_root, "run");
        Directory.CreateDirectory(_runDir);
    }

    public void Dispose() => _ = TestFiles.TryDeleteDirectory(_root); // still held: the OS temp cleanup removes it

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static RunFiles.Entry Entry(string path, byte[] content) =>
        new(path, content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content)));

    // A server that serves the given contents in pieces of chunkSize.
    private static Func<int, long, Task<RunFiles.Chunk?>> Server(IReadOnlyList<byte[]> contents, int chunkSize = 4) => (i, offset) =>
    {
        var c = contents[i];
        var take = (int)Math.Min(chunkSize, c.LongLength - offset);
        return Task.FromResult<RunFiles.Chunk?>(new RunFiles.Chunk(c.AsSpan((int)offset, take).ToArray(), offset + take >= c.LongLength));
    };

    [Fact]
    public async Task WriteAll_WritesEveryFileUnderTheFilesFolder_InPieces()
    {
        byte[][] contents = [Bytes("PNG-bytes-of-a-logo"), Bytes("# Design skill\nUse the tokens."), []];
        RunFiles.Entry[] files = [Entry("logo.png", contents[0]), Entry("skills/design/SKILL.md", contents[1]), Entry("empty.txt", contents[2])];

        (await RunFiles.WriteAllAsync(_runDir, files, Server(contents))).ShouldBeNull();

        var folder = Path.Join(_runDir, ".agentos", "files");
        File.ReadAllBytes(Path.Join(folder, "logo.png")).ShouldBe(contents[0]);
        File.ReadAllText(Path.Join(folder, "skills", "design", "SKILL.md")).ShouldBe("# Design skill\nUse the tokens.");
        File.Exists(Path.Join(folder, "empty.txt")).ShouldBeTrue();
        RunFiles.FilesRoot(_runDir).ShouldBe(Path.GetFullPath(folder));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/tmp/escape.txt")]
    [InlineData("C:\\escape.txt")]
    [InlineData("a\\..\\..\\escape.txt")]
    [InlineData("./a.txt")]
    public async Task WriteAll_TraversalOrAbsolutePath_IsRefused_NothingWrittenOutside(string path)
    {
        var content = Bytes("evil");
        var called = false;
        var problem = await RunFiles.WriteAllAsync(_runDir, [Entry(path, content)], (i, o) => { called = true; return Server([content])(i, o); });

        problem.ShouldNotBeNull();
        problem.ShouldContain("stay inside");
        called.ShouldBeFalse();                                         // never even fetched
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
        RunFiles.Target(_runDir, path).ShouldBeNull();
    }

    [Fact]
    public void Target_AcceptsOnlyTheNormalisedForm()
    {
        RunFiles.Target(_runDir, "art/logo.png").ShouldBe(Path.GetFullPath(Path.Join(_runDir, ".agentos", "files", "art", "logo.png")));
        RunFiles.Target(_runDir, "art\\logo.png").ShouldBeNull();   // the server always sends the normalised path
        RunFiles.Target(_runDir, "art//logo.png").ShouldBeNull();
    }

    [Fact]
    public async Task WriteAll_ShaMismatch_IsRefused_AndThePartialFileRemoved()
    {
        var content = Bytes("real bytes");
        var entry = Entry("logo.png", content) with { Sha256 = new string('0', 64) };

        var problem = await RunFiles.WriteAllAsync(_runDir, [entry], Server([content]));

        problem!.ShouldContain("SHA-256");
        File.Exists(Path.Join(_runDir, ".agentos", "files", "logo.png")).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteAll_BiggerOrSmallerThanAnnounced_IsRefused()
    {
        var content = Bytes("0123456789");
        (await RunFiles.WriteAllAsync(_runDir, [Entry("a.bin", content) with { Size = 4 }], Server([content])))!.ShouldContain("bigger");
        (await RunFiles.WriteAllAsync(_runDir, [Entry("b.bin", content) with { Size = 20 }], Server([content])))!.ShouldContain("not the 20");
    }

    [Fact]
    public async Task WriteAll_ServerRefusesAPiece_FailsTheRun()
    {
        var problem = await RunFiles.WriteAllAsync(_runDir, [Entry("a.txt", Bytes("abc"))], (_, _) => Task.FromResult<RunFiles.Chunk?>(null));
        problem!.ShouldContain("didn't send");
    }

    [Fact]
    public async Task WriteAll_SamePathTwice_OrAnExistingFile_IsNeverOverwritten()
    {
        var a = Bytes("first");
        (await RunFiles.WriteAllAsync(_runDir, [Entry("a.txt", a), Entry("a.txt", a)], Server([a, a])))!.ShouldContain("overwrite");
        File.ReadAllText(Path.Join(_runDir, ".agentos", "files", "a.txt")).ShouldBe("first");
    }

    [Fact]
    public async Task WriteAll_OverTheRunnersCap_IsRefusedBeforeFetching()
    {
        var problem = await RunFiles.WriteAllAsync(_runDir, [new RunFiles.Entry("huge.bin", RunFiles.MaxTotalBytes + 1, new string('0', 64))],
            (_, _) => throw new InvalidOperationException("must not fetch"));
        problem!.ShouldContain("more than this computer takes");
    }

    [Fact]
    public void CleanupDeliveries_RemovesOnlyOldDeliveryFolders()
    {
        var old = Path.Join(_root, "20260101-000000-files-abc");
        var fresh = Path.Join(_root, "20260101-000001-files-def");
        var run = Path.Join(_root, "20260101-000002-run");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(run);
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-10));
        Directory.SetCreationTimeUtc(run, DateTime.UtcNow.AddDays(-10));

        RunFiles.CleanupDeliveries(_root, TimeSpan.FromDays(3));

        Directory.Exists(old).ShouldBeFalse();
        Directory.Exists(fresh).ShouldBeTrue();
        Directory.Exists(run).ShouldBeTrue();   // a CLI run's own directory is its run's business
    }

    // Regression (security review of #290): a Bash rule is a prefix match and acceptEdits confines only the edit tools,
    // so any "cp …" allowance lets the agent copy onto ~/.ssh/authorized_keys. The only copy is the runner's helper.
    [Fact]
    public void ScopedAutonomy_HasNoCpRule_OnlyTheRunnersCopyHelper()
    {
        var rules = ScopedAutonomy.ArgsFor("claude");
        rules.ShouldNotContain(r => r.StartsWith("Bash(cp", StringComparison.Ordinal) || r.StartsWith("PowerShell(cp", StringComparison.Ordinal)
                                    || r.StartsWith("Bash(mv", StringComparison.Ordinal) || r.StartsWith("Bash(install", StringComparison.Ordinal));
        ScopedAutonomy.ClaudeAllowed.ShouldContain("Bash(sh " + RunFiles.CopyHelper + " *)");
        ScopedAutonomy.ClaudeAllowed.Count(r => r.StartsWith("Bash(sh ", StringComparison.Ordinal)).ShouldBe(1);   // no other script
        rules.ShouldContain("PowerShell(sh .agentos/bin/copy-in *)");
        ScopedAutonomy.ClaudeDenied.ShouldContain("WebFetch");   // still nothing downloaded
    }

    // ── Links on the way (a symlink / junction would carry a write out of the run directory) ───────────────────

    [Fact]
    public async Task WriteAll_ALinkedFolderUnderTheFilesRoot_IsRefused_NothingWrittenThroughIt()
    {
        var outside = Path.Join(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Join(_runDir, ".agentos", "files"));
        if (!TryLink(Path.Join(_runDir, ".agentos", "files", "art"), outside)) { Assert.Skip("This machine can't create symbolic links (Windows without Developer Mode)."); }
        var content = Bytes("png");

        var problem = await RunFiles.WriteAllAsync(_runDir, [Entry("art/logo.png", content)], Server([content]));

        problem.ShouldNotBeNull().ShouldContain("is a link");
        Directory.EnumerateFiles(outside).ShouldBeEmpty();
    }

    [Fact]
    public void LinkOnTheWay_PlainFolders_None()
    {
        var deep = Path.Join(_runDir, ".agentos", "files", "a", "b");
        Directory.CreateDirectory(deep);
        RunFiles.LinkOnTheWay(_runDir, deep).ShouldBeNull();
    }

    private static bool TryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // ── copy-in, run for real with sh (Git Bash on Windows, sh on Linux CI) ───────────────────────────────────

    [Fact]
    public void CopyHelper_CopiesATicketFileIntoTheClone_AndMakesItsFolders()
    {
        var sh = Sh();
        var run = CopyRun();
        var (code, output) = CopyIn(sh, run, ".agentos/files/logo.png", "repo/assets/img/logo.png");
        code.ShouldBe(0, output);
        File.ReadAllText(Path.Join(run, "repo", "assets", "img", "logo.png")).ShouldBe("PNG");
        // Inside the clone an existing file may be replaced (the ticket may ask to swap a logo).
        CopyIn(sh, run, ".agentos/files/logo.png", "repo/README.md").Code.ShouldBe(0);
        File.ReadAllText(Path.Join(run, "repo", "README.md")).ShouldBe("PNG");
    }

    [Theory]
    [InlineData(".agentos/files/logo.png", "repo/../../escape.png", "no . or ..")]
    [InlineData(".agentos/files/logo.png", "../escape.png", "no . or ..")]
    [InlineData(".agentos/files/../../secret.txt", "repo/s.txt", "no . or ..")]
    [InlineData("repo/README.md", "repo/copy.md", "not one of the ticket's files")]
    [InlineData(".agentos/files/logo.png", ".agentos/bin/copy-in", "inside .agentos")]
    [InlineData(".agentos/files/logo.png", "notes.txt", "exists outside the cloned repository")]
    [InlineData(".agentos/files/logo.png", "repo/assets/", "not a folder")]
    [InlineData(".agentos/files", "repo/x", "is not a file")]
    public void CopyHelper_RefusesEscapesAndOverwrites(string src, string dst, string why)
    {
        var sh = Sh();
        var run = CopyRun();
        var (code, output) = CopyIn(sh, run, src, dst);
        code.ShouldNotBe(0);
        output.ShouldContain(why);
        File.Exists(Path.Join(_root, "escape.png")).ShouldBeFalse();
        File.ReadAllText(Path.Join(run, "notes.txt")).ShouldBe("mine");
        File.ReadAllText(Path.Join(run, ".agentos", "bin", "copy-in")).ShouldStartWith("#!/bin/sh");
    }

    [Fact]
    public void CopyHelper_AbsoluteDestinationOutside_IsRefused()
    {
        var sh = Sh();
        var run = CopyRun();
        var outside = Path.Join(_root, "outside.png").Replace('\\', '/');
        var (code, output) = CopyIn(sh, run, ".agentos/files/logo.png", outside);
        code.ShouldNotBe(0);
        output.ShouldContain("outside the run folder");
        File.Exists(outside).ShouldBeFalse();
    }

    [Fact]
    public void CopyHelper_ALinkedDestinationFolder_IsRefused()
    {
        var sh = Sh();
        var run = CopyRun();
        var outside = Path.Join(_root, "outside");
        Directory.CreateDirectory(outside);
        if (!TryLink(Path.Join(run, "repo", "linked"), outside)) { Assert.Skip("This machine can't create symbolic links (Windows without Developer Mode)."); }

        var (code, output) = CopyIn(sh, run, ".agentos/files/logo.png", "repo/linked/authorized_keys");

        code.ShouldNotBe(0);
        output.ShouldContain("is a link");
        Directory.EnumerateFiles(outside).ShouldBeEmpty();
    }

    [Fact]
    public void CopyHelper_ALinkedSource_IsRefused()
    {
        var sh = Sh();
        var run = CopyRun();
        var secret = Path.Join(_root, "secret.txt");
        File.WriteAllText(secret, "secret");
        try { File.CreateSymbolicLink(Path.Join(run, ".agentos", "files", "pointer.txt"), secret); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Assert.Skip("This machine can't create symbolic links."); }

        var (code, output) = CopyIn(sh, run, ".agentos/files/pointer.txt", "repo/leak.txt");

        code.ShouldNotBe(0);
        output.ShouldContain("is a link");
        File.Exists(Path.Join(run, "repo", "leak.txt")).ShouldBeFalse();
    }

    // A run directory as the runner leaves it: a delivered file, the helper, a clone, and a file of the run's own.
    private string CopyRun()
    {
        var run = Path.Join(_root, "copy-run");
        Directory.CreateDirectory(Path.Join(run, ".agentos", "files"));
        File.WriteAllText(Path.Join(run, ".agentos", "files", "logo.png"), "PNG");
        Directory.CreateDirectory(Path.Join(run, "repo", ".git"));
        File.WriteAllText(Path.Join(run, "repo", "README.md"), "# repo");
        File.WriteAllText(Path.Join(run, "notes.txt"), "mine");
        RunFiles.WriteCopyHelper(run);
        RunFiles.WriteCopyHelper(run);   // rewritten every delivery
        return run;
    }

    private static (int Code, string Output) CopyIn(string sh, string run, string src, string dst)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(sh)
        {
            WorkingDirectory = run,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(RunFiles.CopyHelper);
        psi.ArgumentList.Add(src);
        psi.ArgumentList.Add(dst);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000).ShouldBeTrue();
        return (p.ExitCode, stdout + stderr.GetAwaiter().GetResult());
    }

    // sh on PATH (Linux, macOS, Git Bash shells), else Git for Windows' sh; skip when there is none.
    private static string Sh()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "sh.exe"), "sh"]
            : ["sh"];
        foreach (var c in candidates)
        {
            if (c != "sh" && !File.Exists(c)) { continue; }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(c) { RedirectStandardOutput = true, UseShellExecute = false };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("echo ok");
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is not null && p.StandardOutput.ReadToEnd().Trim() == "ok" && p.WaitForExit(10_000) && p.ExitCode == 0) { return c; }
            }
            catch (System.ComponentModel.Win32Exception) { /* not this one */ }
        }
        Assert.Skip("No POSIX sh on this machine (install Git for Windows).");
        return "sh";
    }
}
