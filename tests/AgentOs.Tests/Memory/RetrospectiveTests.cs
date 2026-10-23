// Self-improvement: the Retrospective agent turns raw lessons into a project's practices, within guardrails — pinned
// items survive, at most 15 practices, only lessons it was shown can be cleared, and an empty answer changes nothing.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Memory;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Web.Services.Orchestration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Memory;

public sealed class RetrospectiveTests
{
    private static RetrospectiveInput Input(params RetrospectiveItem[] practices) => new("t", "m", "o/r", "#15 Viewer",
        [new("L1", "Acceptance testing failed: E2E needs the AppHost running", false), new("L2", "Checks failed: Node 24.11.1 required", false)],
        practices);

    [Fact]
    public void Parse_KeepsPinnedPractices_AndOnlyShownLessons()
    {
        var r = RetrospectiveAgent.Parse("""
            {"practices":["Start the AppHost before E2E specs","Use Node 24.11.1"],"absorbed":["L1","L9"],"summary":"Two practices."}
            """, Input(new RetrospectiveItem("P1", "Never commit .CBL files", Pinned: true)));

        r.Ran.ShouldBeTrue();
        r.Practices.ShouldBe(["Never commit .CBL files", "Start the AppHost before E2E specs", "Use Node 24.11.1"]);
        r.AbsorbedLessonIds.ShouldBe(["L1"]); // L9 was never shown
    }

    [Fact]
    public void Parse_CapsAtFifteen_AndDropsDuplicates()
    {
        var many = string.Join(",", Enumerable.Range(1, 20).Select(i => $"\"Practice {i}\"").Append("\"practice 1\""));
        RetrospectiveAgent.Parse($$"""{"practices":[{{many}}],"absorbed":[]}""", Input()).Practices.Count.ShouldBe(15);
    }

    [Theory]
    [InlineData("""{"practices":[],"absorbed":["L1"]}""")]
    [InlineData("no json")]
    public void Parse_EmptyOrBroken_DidNotRun(string answer) => RetrospectiveAgent.Parse(answer, Input()).Ran.ShouldBeFalse();

    [Fact]
    public void User_FencesTheLessonsAsData() =>
        RetrospectiveAgent.User(Input()).ShouldContain("<untrusted kind=\"project-memory\">");

    [Fact]
    public void Instructions_OpenWithTheirRoutingKey() =>
        RetrospectiveAgent.Instructions.ShouldStartWith("You are the Retrospective Agent of AgentOS.NET.");

    // ── apply ──

    private sealed class FakeMemory : IMemoryStore
    {
        public List<MemoryNodeInput> Recorded { get; } = [];
        public List<Guid> Forgotten { get; } = [];
        public Task<Guid> RecordAsync(string tenantId, MemoryNodeInput node, CancellationToken ct = default) { Recorded.Add(node); return Task.FromResult(Guid.NewGuid()); }
        public Task LinkAsync(string tenantId, Guid fromId, Guid toId, string rel, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MemoryNode>> RecallLessonsAsync(string tenantId, string repoKey, string query, int max, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryNode>>([]);
        public Task<MemoryGraph> GraphAsync(string tenantId, string? repoKey, int maxNodes, CancellationToken ct = default) => Task.FromResult(new MemoryGraph([], []));
        public Task SetPinnedAsync(string tenantId, Guid nodeId, bool pinned, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetAsync(string tenantId, Guid nodeId, CancellationToken ct = default) { Forgotten.Add(nodeId); return Task.CompletedTask; }
    }

    private static MemoryNode Node(string kind, string text, bool pinned = false) =>
        new(Guid.NewGuid(), kind, kind + ":" + text, text, text, "o/r", pinned, DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task Apply_WritesPractices_RetiresReplacedOnes_ClearsAbsorbedLessons_NeverPinned()
    {
        var memory = new FakeMemory();
        var keptOld = Node(MemoryKinds.Practice, "Use Node 24.11.1");
        var replaced = Node(MemoryKinds.Practice, "Old advice");
        var pinnedOld = Node(MemoryKinds.Practice, "Never commit .CBL files", pinned: true);
        var lesson1 = Node(MemoryKinds.Lesson, "E2E needs the AppHost");
        var pinnedLesson = Node(MemoryKinds.Lesson, "Keep this", pinned: true);
        var result = new RetrospectiveResult(true, ["Never commit .CBL files", "Use Node 24.11.1", "Start the AppHost before E2E specs"], ["L1", "L2"], "s", null);

        var outcome = await RetrospectiveService.ApplyAsync(memory, "t", "o/r", result, [keptOld, replaced, pinnedOld],
            [("L1", lesson1), ("L2", pinnedLesson)], CancellationToken.None);

        memory.Recorded.Count.ShouldBe(3);
        memory.Recorded.ShouldAllBe(n => n.Kind == MemoryKinds.Practice);
        memory.Forgotten.ShouldBe([replaced.Id, lesson1.Id]); // never a pinned practice or lesson
        outcome.ShouldBe(new RetrospectiveOutcome(3, 1, 1));
    }

    [Fact]
    public void PracticesBlock_IsFencedData_AndMarksPinned()
    {
        var block = ProjectMemory.PracticesBlock([Node(MemoryKinds.Practice, "Use Node 24.11.1"), Node(MemoryKinds.Practice, "Never commit .CBL files", pinned: true)]);

        block.ShouldContain("<untrusted kind=\"project-practices\">");
        block.ShouldContain("- Use Node 24.11.1");
        block.ShouldContain("Never commit .CBL files (pinned by the team)");
        ProjectMemory.PracticesBlock([]).ShouldBeEmpty();
    }
}
