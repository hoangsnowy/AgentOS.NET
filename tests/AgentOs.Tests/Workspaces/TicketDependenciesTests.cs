// Ticket dependencies: what a ticket waits for, read from its body, and the order a plan's tickets are created in.

using AgentOs.Domain.Workspaces;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Workspaces;

public sealed class TicketDependenciesTests
{
    [Theory]
    [InlineData("Build it.\n\nDepends on #8, #11", new[] { 8, 11 })]
    [InlineData("**Depends on:** #3 and #2", new[] { 2, 3 })]
    [InlineData("- Blocked by #12", new[] { 12 })]
    [InlineData("Requires #5\nDepends on #5, #7", new[] { 5, 7 })]
    [InlineData("See #4 for context.", new int[0])]                          // a mention is not a dependency
    [InlineData("Depends on: CBL v3 decoder, ChessLibrary module.", new int[0])] // names, not numbers
    [InlineData("Depends on https://github.com/o/r/issues/9", new int[0])]
    [InlineData(null, new int[0])]
    public void Parse_Body_ReturnsTheNumbersItWaitsFor(string? body, int[] expected) =>
        TicketDependencies.Parse(body).ShouldBe(expected);

    [Fact]
    public void Line_RoundTripsThroughParse()
    {
        var line = TicketDependencies.Line([11, 8, 11]);
        line.ShouldBe("Depends on #8, #11");
        TicketDependencies.Parse("text\n\n" + line).ShouldBe([8, 11]);
        TicketDependencies.Line([]).ShouldBeEmpty();
    }

    [Fact]
    public void CreationOrder_PutsDependenciesFirst_StableOtherwise()
    {
        // 0 = UI (needs 2), 1 = docs, 2 = API (needs 3), 3 = module
        int[][] deps = [[2], [], [3], []];

        var order = TicketDependencies.CreationOrder(4, i => deps[i]);

        order.ShouldBe([1, 3, 2, 0]);
    }

    [Fact]
    public void CreationOrder_BadEdgesAndCycles_NeverDropATicket()
    {
        int[][] deps = [[1], [0], [2, 9, -1], []]; // 0↔1 cycle, 2 self + out of range

        var order = TicketDependencies.CreationOrder(4, i => deps[i]);

        order.OrderBy(i => i).ShouldBe([0, 1, 2, 3]);
        order.ToList().IndexOf(2).ShouldBeLessThan(2); // its bad edges are ignored, so it is ready at once
    }
}
