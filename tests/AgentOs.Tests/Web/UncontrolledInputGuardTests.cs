// Regression guard: a text input whose value is rendered from server state on every keystroke
// (value="@_x" @oninput="…") loses keys under latency — the server re-renders a stale value over characters typed
// while the round-trip was in flight. CI caught it on the Activities search ("add key" arrived as "ay"). Text inputs
// with @oninput either use @bind (Blazor's event-field tracking) or stay uncontrolled (no server-written value) and
// reset by swapping the element with @key. Range sliders and checkboxes are exempt (a single value, not typing).

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed partial class UncontrolledInputGuardTests
{
    [GeneratedRegex("""<(input|textarea)\b[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Element();

    [Fact]
    public void TextInputs_NeverRenderValueFromStateWhileHandlingOninput()
    {
        var offenders = Directory.EnumerateFiles(Path.Join(RepoRoot(), "src"), "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .SelectMany(f => Element().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Tag: m.Value)))
            .Where(x => x.Tag.Contains("@oninput", StringComparison.Ordinal)
                     && Regex.IsMatch(x.Tag, """\svalue="@""")
                     && !Regex.IsMatch(x.Tag, """type="(range|checkbox|radio|color)" """.TrimEnd(), RegexOptions.IgnoreCase))
            .Select(x => $"{x.File}: {x.Tag[..Math.Min(x.Tag.Length, 120)]}")
            .ToList();

        offenders.ShouldBeEmpty("Typing into these loses keys under latency — use @bind or an uncontrolled input + @key:\n" + string.Join("\n", offenders));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "AgentOs.slnx"))) { return dir.FullName; }
        }
        throw new InvalidOperationException("Repo root (AgentOs.slnx) not found.");
    }
}
