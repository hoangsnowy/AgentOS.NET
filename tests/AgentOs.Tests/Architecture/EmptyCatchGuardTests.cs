// CodeQL parity for cs/empty-catch-block, which has no Roslyn twin to switch on in .editorconfig: an empty catch body
// (no statement, no comment) anywhere in src/ or tests/ fails this test instead of landing as a code-scanning alert after
// the push. A test that expects a file-system step to fail uses TestFiles.TryIo and asserts (or deliberately ignores)
// the returned outcome; product code says why the failure is safe to ignore.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Architecture;

public sealed partial class EmptyCatchGuardTests
{
    // `catch`, an optional (Type ex) and `when (…)` filter on the same line, then a body with nothing in it.
    [GeneratedRegex(@"\bcatch\b[^{;\n]*\{\s*\}")]
    private static partial Regex EmptyCatch();

    private static readonly string[] Scanned = ["src", "tests"];

    [Fact]
    public void Sources_NeverContainAnEmptyCatchBlock()
    {
        var root = RepoRoot();
        var offenders = Scanned
            .SelectMany(dir => Directory.EnumerateFiles(Path.Join(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                     && !f.EndsWith(nameof(EmptyCatchGuardTests) + ".cs", StringComparison.Ordinal)) // its own samples
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(root, f), Line: i + 1, Text: line)))
            .Where(x => EmptyCatch().IsMatch(x.Text))
            .Select(x => $"{x.File}:{x.Line}: {x.Text.Trim()}")
            .ToList();

        offenders.ShouldBeEmpty("Empty catch blocks (CodeQL cs/empty-catch-block) — record the outcome (TestFiles.TryIo) or handle it:\n"
            + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("try { x(); } catch (IOException) { }")]
    [InlineData("catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {}")]
    [InlineData("catch { }")]
    public void EmptyCatch_MatchesAnEmptyBody(string line) => EmptyCatch().IsMatch(line).ShouldBeTrue();

    [Theory]
    [InlineData("catch (IOException) { /* next start tries again */ }")]
    [InlineData("catch (IOException) { return false; }")]
    [InlineData("catch (InvalidOperationException ex) { _ = ex; } // exited meanwhile")]
    public void EmptyCatch_IgnoresABodyWithAStatementOrAReason(string line) => EmptyCatch().IsMatch(line).ShouldBeFalse();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "AgentOs.slnx"))) { return dir.FullName; }
        }
        throw new InvalidOperationException("Repo root (AgentOs.slnx) not found.");
    }
}
