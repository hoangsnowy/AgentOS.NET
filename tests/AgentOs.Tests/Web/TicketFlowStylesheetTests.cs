// Performance + a11y guards for Workflow › Ticket flow (owner directive 8): nothing in its block loops at rest (the only
// motion is the step spinner of a ticket running now, which stops under reduced motion / Animations off), and every
// class the view uses is defined (no phantom classes).

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed partial class TicketFlowStylesheetTests
{
    private static string Find(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Join([dir.FullName, .. parts]);
            if (File.Exists(candidate)) { return candidate; }
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    private static string Block() => File.ReadAllText(Find("src", "AgentOs.Web", "wwwroot", "orchestration.css")) is var css
        ? css[css.IndexOf("/* ===== Ticket flow", StringComparison.Ordinal)..]
        : "";

    [Fact]
    public void TicketFlowBlock_HasNoAnimationOfItsOwn_AndStopsTheRunningSpinnerWhenMotionIsOff()
    {
        var block = Block();
        block.ShouldNotContain("infinite");
        block.ShouldNotContain("@keyframes");
        block.ShouldContain("@media (prefers-reduced-motion: reduce)");
        block.ShouldContain(":root[data-agent-motion=\"off\"] .step-node .sn-status.stat-running { animation: none;");
    }

    [Fact]
    public void TicketFlowBlock_UsesTokensOnly()
    {
        Block().ShouldNotMatch("#[0-9a-fA-F]{3,8}\\b");
    }

    [Fact]
    public void TicketFlowView_UsesOnlyDefinedClasses()
    {
        var razor = File.ReadAllText(Find("src", "AgentOs.Web", "Components", "Orchestration", "TicketFlowView.razor"));
        var css = File.ReadAllText(Find("src", "AgentOs.Web", "wwwroot", "orchestration.css")) + File.ReadAllText(Find("src", "AgentOs.Web", "wwwroot", "app.css"));
        var used = ClassAttr().Matches(razor).SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(QuotedClass().Matches(razor).SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(c => Token().IsMatch(c))
            .Distinct().ToList();
        used.ShouldContain("tf-root");
        // The card's classes are composed in CardClass/CatOf — every piece must exist too.
        razor.ShouldContain("$\"step-node tf-node cat-{CatOf(node.Kind)} {run}{sel}{bad}\"");
        used.AddRange(["step-node", "tf-node", "tf-has-problem", "is-selected", "run-running", "run-idle", "run-skipped", "cat-agent", "cat-logic", "cat-terminal"]);
        var missing = used.Where(c => !Regex.IsMatch(css, @"\." + Regex.Escape(c) + @"(?![\w-])")).ToList();
        missing.ShouldBeEmpty($"phantom classes: {string.Join(", ", missing)}");
    }

    [GeneratedRegex("class=\"([^\"]+)\"")]
    private static partial Regex ClassAttr();

    [GeneratedRegex("^[a-z][a-z0-9-]*[a-z0-9]$")]
    private static partial Regex Token();

    // Class strings built in code ("syn-btn is-on", "step-node tf-node cat-…").
    [GeneratedRegex("=> [^;]*?\"((?:syn|tf|step|run|cat)-[a-z -]+)\"")]
    private static partial Regex QuotedClass();
}
