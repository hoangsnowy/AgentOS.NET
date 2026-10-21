// Knowledge graph slice 1: what gets written (lessons keyed by content, tagged by source), how it is read back into a
// prompt (fenced, bounded), and how the Board draws a project's graph.

using AgentOs.Domain.Memory;
using AgentOs.Web.Services.Knowledge;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class KnowledgeTests
{
    private static MemoryNode N(string kind, string key, string repo = "o/r", string title = "t", bool pinned = false) =>
        new(Guid.NewGuid(), kind, key, title, null, repo, pinned, DateTimeOffset.UtcNow, null);

    [Theory]
    [InlineData("The pull request's automated checks failed. Fix the cause…", "checks")]
    [InlineData("Acceptance testing failed. Make these criteria pass…", "testing")]
    [InlineData("Reload the page before the duplicate check.", "review")]
    public void SourceOf_ReadsTheRequestWording(string text, string source) => ProjectMemory.SourceOf(text).ShouldBe(source);

    [Fact]
    public async Task RecordLesson_SameTextOnTheSameRepo_IsOneLesson_LinkedToItsPullRequest()
    {
        var store = Substitute.For<IMemoryStore>();
        var keys = new List<string>();
        store.RecordAsync("t1", Arg.Do<MemoryNodeInput>(n => keys.Add(n.Key)), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        await ProjectMemory.RecordLessonAsync(store, "t1", "o/r", "https://github.com/o/r/pull/22", "Reload before the duplicate check.");
        await ProjectMemory.RecordLessonAsync(store, "t1", "o/r", "https://github.com/o/r/pull/22", "  Reload before the duplicate check.  ");

        keys.Where(k => k.StartsWith("lesson:", StringComparison.Ordinal)).Distinct().ShouldHaveSingleItem();
        await store.Received(2).LinkAsync("t1", Arg.Any<Guid>(), Arg.Any<Guid>(), MemoryRels.LearnedFrom, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Writes_NeverThrow()
    {
        var store = Substitute.For<IMemoryStore>();
        store.RecordAsync(default!, default!, default).ReturnsForAnyArgs<Task<Guid>>(_ => throw new InvalidOperationException("db down"));

        await ProjectMemory.RecordLessonAsync(store, "t1", "o/r", null, "x");
        await ProjectMemory.RecordChangeAsync(store, "t1", "o", "r", 7, "t", "https://github.com/o/r/pull/1", "feat: x (#7)", null);
    }

    [Fact]
    public void PromptBlock_IsFencedAndBounded_AndEmptyWithoutLessons()
    {
        ProjectMemory.PromptBlock([]).ShouldBeEmpty();
        var lesson = N(MemoryKinds.Lesson, "lesson:1", title: "[checks] Pin Node 24.11.1", pinned: true) with { Body = new string('x', 2000) };

        var block = ProjectMemory.PromptBlock([lesson]);

        block.ShouldContain("<untrusted kind=\"project-memory\">");
        block.ShouldContain("(pinned by the team)");
        block.Length.ShouldBeLessThan(1200);
    }

    [Fact]
    public void Layout_RepoInTheCentre_ThenTicketsChangesLessonsOnRings_OtherReposLeftOut()
    {
        var repo = N(MemoryKinds.Repo, "repo:o/r");
        var ticket = N(MemoryKinds.Ticket, "ticket:o/r#7");
        var pr = N(MemoryKinds.PullRequest, "pr:https://github.com/o/r/pull/17");
        var lesson = N(MemoryKinds.Lesson, "lesson:a");
        var elsewhere = N(MemoryKinds.Ticket, "ticket:x/y#1", repo: "x/y");
        var graph = new MemoryGraph([repo, ticket, pr, lesson, elsewhere],
        [
            new(ticket.Id, repo.Id, MemoryRels.InRepo), new(ticket.Id, pr.Id, MemoryRels.SolvedBy),
            new(pr.Id, repo.Id, MemoryRels.InRepo), new(lesson.Id, pr.Id, MemoryRels.LearnedFrom),
        ]);

        var picture = KnowledgeLayout.Compute(graph, "o/r");

        picture.Nodes.Count.ShouldBe(4);
        Radius(picture, repo).ShouldBe(0, 0.5);
        Radius(picture, ticket).ShouldBe(KnowledgeLayout.TicketRing, 0.5);
        Radius(picture, pr).ShouldBe(KnowledgeLayout.ChangeRing, 0.5);
        Radius(picture, lesson).ShouldBe(KnowledgeLayout.LessonRing, 0.5);
        picture.Edges.Count.ShouldBe(4);
    }

    private static double Radius(KnowledgePicture p, MemoryNode n)
    {
        var placed = p.Nodes.Single(x => x.Node.Id == n.Id);
        return Math.Sqrt(placed.X * placed.X + placed.Y * placed.Y);
    }
}
