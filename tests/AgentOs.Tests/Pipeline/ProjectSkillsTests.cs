// A scoped run may now change the project's skills (.claude/skills/**) — VNChess #136 needed a one-section edit to
// .claude/skills/thu-vien-co-design/SKILL.md and nobody could approve it in claude -p. A skill is an instruction every
// later agent follows, so a change to one is reviewed like code: the Reviewer is told (named files when the server could
// list the change, a conditional instruction when it couldn't, nothing when it touches none) and the task's feed names them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Web.Services.BoardTasks;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Pipeline;

public sealed class ProjectSkillsTests
{
    private const string Skill = ".claude/skills/thu-vien-co-design/SKILL.md";

    [Theory]
    [InlineData(Skill, true)]
    [InlineData(".claude/skills/x/references/tokens.md", true)]
    [InlineData("web/.claude/skills/x/SKILL.md", true)]          // a nested project
    [InlineData(@".claude\skills\x\SKILL.md", true)]
    [InlineData("./.CLAUDE/Skills/x/SKILL.md", true)]
    [InlineData(".claude/skills/", false)]
    [InlineData(".claude/settings.json", false)]
    [InlineData(".claude/commands/x.md", false)]
    [InlineData("docs/claude/skills/x.md", false)]
    [InlineData("src/my.claude/skills/x.md", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSkillPath_OnlyFilesUnderClaudeSkills(string? path, bool expected) => ProjectSkills.IsSkillPath(path).ShouldBe(expected);

    [Fact]
    public void Of_KeepsTheSkillFilesInOrder_Distinct() =>
        ProjectSkills.Of(["src/a.cs", Skill, ".claude/settings.json", ".claude/skills/b/SKILL.md", Skill])
            .ShouldBe([Skill, ".claude/skills/b/SKILL.md"]);

    [Fact]
    public void FeedNote_NamesTheFilesAndBoundsTheList()
    {
        var many = Enumerable.Range(1, ProjectSkills.MaxNamed + 3).Select(i => $".claude/skills/s{i}/SKILL.md").ToList();

        ProjectSkills.FeedNote("hoangsnowy/vnchess", [Skill]).ShouldBe(
            "hoangsnowy/vnchess — this change edits the project's skills (instructions every agent on this project follows): "
            + Skill + ". They are reviewed like code.");
        ProjectSkills.FeedNote("o/r", many).ShouldContain("and 3 more");
        ProjectSkills.FeedNote("o/r", many).ShouldNotContain($"s{ProjectSkills.MaxNamed + 1}/");
    }

    // ── the Reviewer ────────────────────────────────────────────────────────────────────────────────────────────

    private static AcceptanceRequest Ticket136(IReadOnlyList<string>? skills) =>
        new(Guid.NewGuid(), "t", "m", new WorkRepo(Guid.NewGuid(), "hoangsnowy", "vnchess", "main", "issue-136-ai-fix"), 136,
            "Board pieces follow the design skill", "## Acceptance criteria\n- [ ] The design skill names the piece set.")
        { ChangedSkills = skills };

    [Fact]
    public void ReviewerPrompt_DiffTouchesSkills_ReviewsThemLikeCode()
    {
        var user = ReviewAgent.User(Ticket136([Skill]));

        user.ShouldContain("This change edits the project's skills: " + Skill);
        user.ShouldContain("review a skill diff as carefully as code");
        user.ShouldContain("weakens governance");
        user.ShouldContain("exfiltration");
        user.ShouldContain("contradicts the repository's AGENTS.md");
        user.TrimEnd().ShouldEndWith(ReviewAgent.ReviewAsk); // the ask stays the last line
    }

    [Fact]
    public void ReviewerPrompt_ChangedFilesUnknown_SaysItConditionally()
    {
        var user = ReviewAgent.User(Ticket136(null));

        user.ShouldContain("If the diff touches the project's skills (.claude/skills/**)");
        user.ShouldContain("review a skill diff as carefully as code");
        user.TrimEnd().ShouldEndWith(ReviewAgent.ReviewAsk);
    }

    [Fact]
    public void ReviewerPrompt_DiffTouchesNoSkill_SaysNothingAboutThem() =>
        ReviewAgent.User(Ticket136([])).ShouldNotContain("Project skills.");

    // ── what the server learns from the pull request ──────────────────────────────────────────────────────────────

    private static readonly WorkspaceDescriptor Vnchess =
        new(Guid.NewGuid(), "t", SourceProviderKind.GitHub, "hoangsnowy", "vnchess", null, "main", "token");

    [Fact]
    public async Task ChangedSkillsAsync_FiltersThePullRequestsFiles()
    {
        var prs = new FakePrs(["web/src/Board.tsx", Skill, ".claude/settings.json"]);

        (await BoardTaskService.ChangedSkillsAsync(prs, Vnchess, 137, TestContext.Current.CancellationToken)).ShouldBe([Skill]);
        prs.AskedFor.ShouldBe(137);
    }

    [Fact]
    public async Task ChangedSkillsAsync_UnknownFilesOrNoPullRequest_IsNull()
    {
        (await BoardTaskService.ChangedSkillsAsync(new FakePrs(null), Vnchess, 137, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await BoardTaskService.ChangedSkillsAsync(new FakePrs([Skill]), Vnchess, null, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    private sealed class FakePrs(IReadOnlyList<string>? files) : IPrCreationService
    {
        public int? AskedFor { get; private set; }

        public Task<PrCreationResult> CreateFromBranchAsync(WorkspaceDescriptor workspace, string branch, string title, string body,
            CancellationToken ct = default) => Task.FromResult(new PrCreationResult(false, null, null, "not used"));

        public Task<IReadOnlyList<string>?> ListChangedFilesAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default)
        {
            AskedFor = number;
            return Task.FromResult(files);
        }
    }
}
