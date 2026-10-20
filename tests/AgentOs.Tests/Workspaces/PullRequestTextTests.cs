// The first live run on a real project opened "fix: #7 #7 Add xiangqi core…" with no link back to the issue.

using AgentOs.Domain.Workspaces;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Workspaces;

public sealed class PullRequestTextTests
{
    [Theory]
    [InlineData(7, "#7 Add xiangqi core", "type:feature", "feat: Add xiangqi core (#7)")]
    [InlineData(7, "Add xiangqi core", null, "feat: Add xiangqi core (#7)")]
    [InlineData(12, "#12: Board crashes on empty FEN", "type:bug", "fix: Board crashes on empty FEN (#12)")]
    [InlineData(3, "#3 — Write the import guide", "docs", "docs: Write the import guide (#3)")]
    [InlineData(null, "Tidy the build", "type:chore", "chore: Tidy the build")]
    [InlineData(5, "#5", null, "feat: Agent change (#5)")]
    public void Title_TicketTitleAndType_ConventionalWithIssueOnce(int? issue, string title, string? type, string expected) =>
        PullRequestText.Title(issue, title, type).ShouldBe(expected);

    [Fact]
    public void Body_WithIssue_EndsWithClosesLine() =>
        PullRequestText.Body(7, "  Adds the core.  ").ShouldBe("Adds the core.\n\nCloses #7");

    [Fact]
    public void Body_NoSummaryNoIssue_FallsBackToAPlainSentence() =>
        PullRequestText.Body(null, " ").ShouldBe("Change prepared by an AgentOS.NET agent.");

    [Theory]
    [InlineData("TYPE: Bug", "fix")]
    [InlineData("perf", "perf")]
    [InlineData("type:spike", "feat")]
    [InlineData(null, "feat")]
    public void ConventionalType_Label_MapsToCommitType(string? label, string expected) =>
        PullRequestText.ConventionalType(label).ShouldBe(expected);
}
