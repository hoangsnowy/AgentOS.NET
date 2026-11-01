// The durable evidence log keeps what a member's-computer row adds (origin, agent role, runner, policy source,
// refused) and answers the two new reads — "On a member's computer" (optionally one task) and the Insights per-role
// fold — strictly per tenant (EF in-memory provider; the buffered decorator delegates both reads to it).

using System;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;
using AgentOs.Modules.Tools.Evidence;
using AgentOs.Modules.Tools.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tools;

public sealed class MemberComputerEvidenceStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static ToolInvocationEvidence Computer(string id, string tenant, string run, string? session, string role, bool denied = false, DateTimeOffset? at = null) =>
        new(id, "Bash", tenant, run, "{\"summary\":\"dotnet test\"}", denied ? "Refused" : "Ran", denied, at ?? T0, (at ?? T0).AddSeconds(1), session)
        {
            Origin = ToolEvidenceOrigins.MemberComputer,
            AgentRole = role,
            RunnerId = "8b1f0c3e-0000-0000-0000-000000000001",
            PolicySource = ToolEvidencePolicies.CliScopedPermissions,
            Denied = denied,
        };

    private static ToolInvocationEvidence Server(string id, string tenant) =>
        new(id, "build_verifier", tenant, "run-s", "{}", "ok", false, T0, T0.AddSeconds(1));

    [Fact]
    public async Task EfLog_PersistsTheMemberComputerFields()
    {
        var log = NewLog();
        await log.AppendAsync(Computer("toolu_1", "t1", "req-1", "sess-1", "Coding", denied: true));

        var row = (await log.ListRecentAsync("t1")).ShouldHaveSingleItem();

        row.Origin.ShouldBe(ToolEvidenceOrigins.MemberComputer);
        row.AgentRole.ShouldBe("Coding");
        row.RunnerId.ShouldBe("8b1f0c3e-0000-0000-0000-000000000001");
        row.PolicySource.ShouldBe(ToolEvidencePolicies.CliScopedPermissions);
        row.Denied.ShouldBeTrue();
        row.InputSummary.ShouldBe("dotnet test");
    }

    [Fact]
    public async Task EfLog_FilterByOriginAndSession_StaysInTheTenant()
    {
        var log = NewLog();
        await log.AppendAsync(Server("srv-1", "t1"));
        await log.AppendAsync(Computer("toolu_1", "t1", "req-1", "sess-1", "Coding"));
        await log.AppendAsync(Computer("toolu_2", "t1", "req-2", "sess-2", "Testing"));
        await log.AppendAsync(Computer("toolu_3", "t2", "req-3", "sess-1", "Coding")); // same session id, other tenant

        var onComputer = await log.ListRecentAsync("t1", new ToolEvidenceFilter(ToolEvidenceOrigins.MemberComputer));
        var oneTask = await log.ListRecentAsync("t1", new ToolEvidenceFilter(ToolEvidenceOrigins.MemberComputer, "sess-1"));

        onComputer.Select(r => r.CallId).ShouldBe(["toolu_1", "toolu_2"], ignoreOrder: true);
        oneTask.ShouldHaveSingleItem().CallId.ShouldBe("toolu_1");
        (await log.ListRecentAsync("t3", new ToolEvidenceFilter(ToolEvidenceOrigins.MemberComputer))).ShouldBeEmpty();
    }

    [Fact]
    public async Task EfLog_SummarizeMemberComputer_GroupsByRoleCountsRunsCallsDenials()
    {
        var log = NewLog();
        await log.AppendAsync(Computer("a1", "t1", "req-1", "s", "Coding"));
        await log.AppendAsync(Computer("a2", "t1", "req-1", "s", "Coding", denied: true));
        await log.AppendAsync(Computer("a3", "t1", "req-2", "s", "Coding"));
        await log.AppendAsync(Computer("b1", "t1", "req-3", "s", "Testing", denied: true));
        await log.AppendAsync(Computer("old", "t1", "req-0", "s", "Testing", at: T0.AddDays(-60)));
        await log.AppendAsync(Server("srv", "t1"));
        await log.AppendAsync(Computer("x1", "t2", "req-9", "s", "Coding"));

        var summary = await log.SummarizeMemberComputerAsync("t1", T0.AddDays(-30));

        summary.ShouldBe([new ToolCallsByRole("Coding", 2, 3, 1), new ToolCallsByRole("Testing", 1, 1, 1)]);
        summary[0].CallsPerRun.ShouldBe(1.5);
        summary[0].DenialsPerRun.ShouldBe(0.5);
        (await log.SummarizeMemberComputerAsync("t2", null)).ShouldHaveSingleItem().ShouldBe(new ToolCallsByRole("Coding", 1, 1, 0));
    }

    [Fact]
    public async Task BufferedLog_DelegatesTheNewReadsToItsSink()
    {
        var inner = new InMemoryToolInvocationLog();
        await using var buffered = new BufferedToolInvocationLog(inner, null, capacity: 16, retryDelay: TimeSpan.Zero);
        await buffered.AppendAsync(Computer("toolu_1", "t1", "req-1", "sess-1", "Coding"));
        await buffered.DrainPendingAsync();

        (await buffered.ListRecentAsync("t1", new ToolEvidenceFilter(ToolEvidenceOrigins.MemberComputer, "sess-1"))).ShouldHaveSingleItem();
        (await buffered.SummarizeMemberComputerAsync("t1", null)).ShouldHaveSingleItem().ToolCalls.ShouldBe(1);
    }

    [Fact]
    public void ToolCallsByRole_Fold_IgnoresOtherOriginsAndOlderRows()
    {
        var rows = new[]
        {
            Computer("a", "t1", "r1", null, "Reviewer"),
            Computer("b", "t1", "r1", null, "Reviewer", denied: true),
            Computer("c", "t1", "r0", null, "Reviewer", at: T0.AddDays(-2)),
            Server("d", "t1"),
        };

        ToolCallsByRole.Fold(rows, ToolEvidenceOrigins.MemberComputer, T0.AddDays(-1))
            .ShouldHaveSingleItem().ShouldBe(new ToolCallsByRole("Reviewer", 1, 2, 1));
    }

    [Fact]
    public void InputSummary_ServerRowWithASecret_IsRedacted()
    {
        var row = new ToolInvocationEvidence("c", "http", "t1", null, "{\"url\":\"https://x\",\"headers\":\"Authorization: Bearer abcdefghijklmnopqrstu\"}", "ok", false, T0, T0);

        row.InputSummary.ShouldNotContain("abcdefghijklmnopqrstu");
    }

    [Fact]
    public void InputSummary_ServerRowWithASummaryField_IsNotReadAsAMemberComputerRow()
    {
        // Only a member-computer row is the recorded {"summary": …} shape; a server tool's input is shown as-is.
        var server = new ToolInvocationEvidence("c", "notes", "t1", null, "{\"summary\":\"short\",\"body\":\"more\"}", "ok", false, T0, T0);
        var member = server with { Origin = ToolEvidenceOrigins.MemberComputer };

        server.InputSummary.ShouldBe("{\"summary\":\"short\",\"body\":\"more\"}");
        server.RequestedAutonomy.ShouldBeNull();
        member.InputSummary.ShouldBe("short");
    }

    [Theory]
    [InlineData("scoped")]
    [InlineData(null)]
    public void MemberComputerInput_RoundTrips(string? requested)
    {
        var input = MemberComputerInput.Write("git push \"origin\" main", requested);

        MemberComputerInput.TryRead(input, out var summary, out var back).ShouldBeTrue();
        summary.ShouldBe("git push \"origin\" main");
        back.ShouldBe(requested);
        MemberComputerInput.TryRead("{\"other\":1}", out _, out _).ShouldBeFalse();
        MemberComputerInput.TryRead("not json {", out _, out _).ShouldBeFalse();
    }

    private static EfToolInvocationLog NewLog()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var root = new InMemoryDatabaseRoot();
        var name = "member-computer-" + Guid.NewGuid().ToString("N");
        services.AddDbContext<ToolsDbContext>(o => o.UseInMemoryDatabase(name, root));
        var sp = services.BuildServiceProvider();
        return new EfToolInvocationLog(sp, sp.GetRequiredService<ILogger<EfToolInvocationLog>>());
    }
}
