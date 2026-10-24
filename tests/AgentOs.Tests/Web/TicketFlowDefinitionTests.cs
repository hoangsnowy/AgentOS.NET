// The ticket flow as data (ADR-0014): the default definition is today's flow, every safety invariant refuses a
// definition that breaks it with a plain-language reason, label filters resolve per ticket, and the JSON round-trips.

using System.Linq;
using AgentOs.Modules.Pipeline.Agents;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class TicketFlowDefinitionTests
{
    private static TicketFlowDefinition Default => TicketFlowDefinition.Default;

    private static TicketFlowDefinition WithNode(string id, System.Func<TicketFlowNode, TicketFlowNode> change) =>
        Default.With(change(Default.Find(id)!));

    private static string Reasons(TicketFlowDefinition d) => string.Join(" | ", TicketFlowValidator.Validate(d).Select(p => p.Message));

    [Fact]
    public void Default_IsTodaysFlow_AndValid()
    {
        Default.Summary().ShouldBe("Code → Checks → Review → Test → Merge");
        TicketFlowValidator.Validate(Default).ShouldBeEmpty();
        Default.Edges.Where(e => e.When == TicketFlowEdgeWhen.Fail).Select(e => e.From).ShouldBe(["checks", "review", "test"]);
        Default.Edges.Where(e => e.When == TicketFlowEdgeWhen.Fail).ShouldAllBe(e => e.To == "code");
        TicketFlowPlan.Default.Summary.ShouldBe("Code → Checks → Review → Test → Merge");
        TicketFlowPlan.Default.SafetyProblem().ShouldBeNull();
    }

    [Fact]
    public void Json_RoundTrips_WithStepKindsByName()
    {
        var custom = WithNode("review", n => n with { SkipLabels = ["type:chore"] });
        var json = custom.ToJson();

        json.ShouldContain("\"kind\":\"Review\"");
        var back = TicketFlowDefinition.FromJson(json)!;
        back.ToJson().ShouldBe(json);
        back.Find("review")!.Skip.ShouldBe(["type:chore"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{\"nodes\":[{\"id\":\"x\",\"kind\":\"Deploy\"}],\"edges\":[]}")]
    public void FromJson_Unreadable_IsNull_AndRefused(string json)
    {
        TicketFlowDefinition.FromJson(json).ShouldBeNull();
        TicketFlowValidator.Validate(TicketFlowDefinition.FromJson(json)).ShouldNotBeEmpty();
    }

    // ── Invariant: starts at Code ──
    [Fact]
    public void MissingCode_IsRefused()
    {
        var d = TicketFlowDefinition.Chain(Default.Nodes.Where(n => n.Kind != TicketStepKind.Code).ToList());
        Reasons(d).ShouldContain("must start with Code");
    }

    [Fact]
    public void AStepBeforeCode_IsRefused()
    {
        var d = Default with { Edges = [.. Default.Edges, new("merge", "code", TicketFlowEdgeWhen.Pass)] };
        Reasons(d).ShouldContain("Nothing may come before Code");
    }

    // ── Invariant: ends at Merge ──
    [Fact]
    public void MissingMerge_IsRefused()
    {
        var d = TicketFlowDefinition.Chain(Default.Nodes.Where(n => n.Kind != TicketStepKind.Merge).ToList());
        Reasons(d).ShouldContain("must end with Merge");
    }

    // ── Invariant: Checks always runs before Merge ──
    [Fact]
    public void ChecksRemoved_TurnedOff_OrLimited_IsRefused()
    {
        Reasons(TicketFlowDefinition.Chain(Default.Nodes.Where(n => n.Kind != TicketStepKind.Checks).ToList()))
            .ShouldContain("Checks must run before Merge");
        Reasons(WithNode("checks", n => n with { Enabled = false })).ShouldContain("Checks always runs");
        Reasons(WithNode("checks", n => n with { SkipLabels = ["type:docs"] })).ShouldContain("can't be limited by labels");
    }

    [Fact]
    public void CodeAndMerge_CantBeTurnedOff()
    {
        Reasons(WithNode("code", n => n with { Enabled = false })).ShouldContain("Code always runs");
        Reasons(WithNode("merge", n => n with { Enabled = false })).ShouldContain("Merge always runs");
    }

    // ── Invariant: at least one of Review / Test for every ticket ──
    [Fact]
    public void BothGatesOff_IsRefused()
    {
        var d = WithNode("review", n => n with { Enabled = false });
        d = d.With(d.Find("test")! with { Enabled = false });
        Reasons(d).ShouldContain("Turn on Review or Test");
    }

    [Fact]
    public void OneGateOff_IsAllowed()
    {
        TicketFlowValidator.Validate(WithNode("review", n => n with { Enabled = false })).ShouldBeEmpty();
        TicketFlowValidator.Validate(WithNode("test", n => n with { Enabled = false })).ShouldBeEmpty();
    }

    [Fact]
    public void Filters_ThatLeaveATicketWithNoGate_AreRefused_NamingTheTicket()
    {
        // Review skipped for chores, Test skipped for docs: a ticket labelled both would reach Merge unreviewed and untested.
        var d = WithNode("review", n => n with { SkipLabels = ["type:chore"] });
        d = d.With(d.Find("test")! with { SkipLabels = ["type:docs"] });

        Reasons(d).ShouldBe("A ticket labelled type:chore and type:docs would reach Merge with neither Review nor Test.");
    }

    [Fact]
    public void OnlyFilters_ThatLeaveAnUnlabelledTicketWithNoGate_AreRefused()
    {
        var d = WithNode("review", n => n with { Enabled = false });
        d = d.With(d.Find("test")! with { OnlyLabels = ["type:feature"] });

        Reasons(d).ShouldContain("A ticket with none of the labels type:feature would reach Merge");
    }

    [Fact]
    public void Filters_ThatAlwaysLeaveAGate_AreAllowed()
    {
        TicketFlowValidator.Validate(WithNode("review", n => n with { SkipLabels = ["type:chore"] })).ShouldBeEmpty();
        var d = WithNode("review", n => n with { SkipLabels = ["type:docs"] });
        d = d.With(d.Find("test")! with { OnlyLabels = ["type:docs"] });
        TicketFlowValidator.Validate(d).ShouldBeEmpty(); // docs → tested, everything else → reviewed
    }

    // ── Invariant: no cycles except fail → Code ──
    [Fact]
    public void APassEdgeLoopingBack_IsRefused()
    {
        var d = Default with
        {
            Edges = [.. Default.Edges.Where(e => !(e.From == "test" && e.When == TicketFlowEdgeWhen.Pass)), new("test", "checks", TicketFlowEdgeWhen.Pass)],
        };
        Reasons(d).ShouldNotBeEmpty();
    }

    [Fact]
    public void AFailEdgeNotToCode_IsRefused()
    {
        var d = Default with { Edges = [.. Default.Edges.Where(e => !(e.From == "test" && e.When == TicketFlowEdgeWhen.Fail)), new("test", "review", TicketFlowEdgeWhen.Fail)] };
        Reasons(d).ShouldContain("A failed Test can only go back to Code");
    }

    [Fact]
    public void AStepThatCanFailWithoutAWayBack_IsRefused()
    {
        var d = Default with { Edges = Default.Edges.Where(e => !(e.From == "review" && e.When == TicketFlowEdgeWhen.Fail)).ToList() };
        Reasons(d).ShouldContain("Review needs its way back to Code");
    }

    // ── Invariant: every node reachable ──
    [Fact]
    public void AnUnreachableStep_IsRefused()
    {
        var d = Default with
        {
            Edges = [.. Default.Edges.Where(e => !(e.When == TicketFlowEdgeWhen.Pass && e.From is "checks" or "review")), new("checks", "test", TicketFlowEdgeWhen.Pass),
                new("review", "test", TicketFlowEdgeWhen.Pass)],
        };
        Reasons(d).ShouldContain("Review can't be reached from Code");
    }

    // ── Structure ──
    [Fact]
    public void StepsOutOfOrder_AreRefused()
    {
        var n = Default.Nodes;
        var d = TicketFlowDefinition.Chain([n[0], n[1], n[3], n[2], n[4]]);
        Reasons(d).ShouldContain("Review must come before Test");
    }

    [Fact]
    public void DuplicateKinds_AndIds_AreRefused()
    {
        Reasons(TicketFlowDefinition.Chain([.. Default.Nodes.Take(3), new("review2", TicketStepKind.Review), .. Default.Nodes.Skip(3)]))
            .ShouldContain("only one Review step");
        Reasons(Default with { Nodes = [.. Default.Nodes, Default.Nodes[0]] }).ShouldContain("share the id code");
    }

    [Fact]
    public void ARoleThatCantRunTheStep_IsRefused()
    {
        Reasons(WithNode("review", n => n with { Role = AgentRoles.Acceptance })).ShouldContain("Review can't be run by Testing (tickets) — pick Reviewer");
        Reasons(WithNode("review", n => n with { Role = "Nobody" })).ShouldContain("unknown agent");
        Reasons(WithNode("checks", n => n with { Role = AgentRoles.Reviewer })).ShouldContain("isn't run by an agent");
        TicketFlowValidator.Validate(WithNode("review", n => n with { Role = AgentRoles.Reviewer })).ShouldBeEmpty();
    }

    [Fact]
    public void BadLabels_AreRefused()
    {
        Reasons(WithNode("review", n => n with { SkipLabels = [" type:chore"] })).ShouldContain("stray spaces");
        Reasons(WithNode("review", n => n with { SkipLabels = ["a", "b", "c", "d", "e", "f", "g", "h", "i"] })).ShouldContain("at most 8 labels");
        Reasons(WithNode("review", n => n with { SkipLabels = ["x"], OnlyLabels = ["X"] })).ShouldContain("both requires and skips x");
    }

    // ── Resolution per ticket ──
    [Fact]
    public void Resolve_SkipsWhatTheLabelsSay_CaseInsensitive()
    {
        var d = WithNode("review", n => n with { SkipLabels = ["type:chore"] });

        d.Resolve(["Type:Chore"]).Summary.ShouldBe("Code → Checks → Test → Merge");
        d.Resolve(["type:chore"]).Skipped.ShouldHaveSingleItem().Id.ShouldBe("review");
        d.Resolve(["type:feature"]).Summary.ShouldBe("Code → Checks → Review → Test → Merge");
        d.Resolve(null).Runs(TicketStepKind.Review).ShouldBeTrue();
        d.Summary().ShouldBe("Code → Checks → Review (not type:chore) → Test → Merge");
    }

    [Fact]
    public void Plan_WithoutAGate_IsUnsafe()
    {
        var unsafePlan = new TicketFlowPlan([Default.Nodes[0], Default.Nodes[1], Default.Nodes[4]], []);
        unsafePlan.SafetyProblem()!.ShouldContain("neither Review nor Test");
        new TicketFlowPlan([Default.Nodes[0], Default.Nodes[2], Default.Nodes[4]], []).SafetyProblem()!.ShouldContain("Checks");
    }

    [Fact]
    public void EveryStepKind_IsRegistered_WithRolesFromTheAgentRegistry()
    {
        foreach (var info in System.Enum.GetValues<TicketStepKind>().Select(TicketStepKinds.Of))
        {
            info.Roles.ShouldAllBe(r => AgentRoles.Find(r) != null);
        }
        TicketStepKinds.Of(TicketStepKind.Code).DefaultRole.ShouldBe(AgentRoles.IssueWork);
        TicketStepKinds.Of(TicketStepKind.Review).DefaultRole.ShouldBe(AgentRoles.Reviewer);
        TicketStepKinds.Of(TicketStepKind.Test).DefaultRole.ShouldBe(AgentRoles.Acceptance);
    }
}
