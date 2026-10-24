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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

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

    [Fact]
    public void ScopedAutonomy_LetsTheAgentCopyTicketFiles_NotAnythingElse()
    {
        ScopedAutonomy.ClaudeAllowed.ShouldContain("Bash(cp ../.agentos/files/*)");
        ScopedAutonomy.ClaudeAllowed.ShouldContain("Bash(cp -r ../.agentos/files/*)");
        ScopedAutonomy.ClaudeAllowed.ShouldNotContain(r => r == "Bash(cp *)" || r == "Bash(cp*)");
        ScopedAutonomy.ClaudeDenied.ShouldContain("WebFetch");   // still nothing downloaded
        ScopedAutonomy.ArgsFor("claude").ShouldContain("PowerShell(cp ../.agentos/files/*)");
    }
}
