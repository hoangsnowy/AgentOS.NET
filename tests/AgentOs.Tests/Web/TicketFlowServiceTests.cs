// Ticket flow storage + the service on top (ADR-0014): versions per tenant (who / when), revert as a new version, the
// board's own flow wins over the tenant's, a stored flow that breaks a safety rule is refused at save AND at run time
// (the built-in flow runs), only admins change it, and two tenants never see each other's flows — on the in-memory
// store (standalone Web) and the EF store (EF Core InMemory; the real-Postgres RLS check is RowLevelSecurityRealDbTests).

using System.Linq;
using System.Threading.Tasks;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Repositories;
using AgentOs.Tests.Identity;
using AgentOs.Web.Services.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class TicketFlowServiceTests
{
    private static readonly TicketFlowActor Admin = new("u-admin", "Ada Admin", IsAdmin: true);
    private static readonly TicketFlowActor Member = new("u-member", "Mo Member", IsAdmin: false);

    public static TheoryData<string> Stores => ["memory", "ef"];

    private static TicketFlowService Service(string store)
    {
        var services = new ServiceCollection();
        if (store == "ef")
        {
            var db = $"tf-{Guid.NewGuid()}";
            services.AddDbContext<WorkspacesDbContext>(o => o.UseInMemoryDatabase(db));
            services.AddSingleton<AgentOs.SharedKernel.Identity.ITenantContext>(new TestTenantContext());
            services.AddScoped<ITicketFlowStore, EfTicketFlowStore>();
        }
        else
        {
            services.AddSingleton<ITicketFlowStore, InMemoryTicketFlowStore>();
        }
        return new TicketFlowService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<TicketFlowService>.Instance);
    }

    private static TicketFlowDefinition NoReviewForChores() =>
        TicketFlowDefinition.Default.With(TicketFlowDefinition.Default.Find("review")! with { SkipLabels = ["type:chore"] });

    private static TicketFlowDefinition TestOff() =>
        TicketFlowDefinition.Default.With(TicketFlowDefinition.Default.Find("test")! with { Enabled = false });

    [Fact]
    public async Task Save_LosingTheVersionRaceRepeatedly_SaysSoInsteadOfThrowing()
    {
        // Review of #285: four saves racing the unique (TenantId, Version) index used to surface an unhandled
        // DbUpdateException in the admin's circuit.
        var store = NSubstitute.Substitute.For<ITicketFlowStore>();
        NSubstitute.SubstituteExtensions.Returns(
            store.AppendAsync(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<Guid>(), NSubstitute.Arg.Any<string?>(), NSubstitute.Arg.Any<string?>(),
                NSubstitute.Arg.Any<string?>(), NSubstitute.Arg.Any<string?>(), NSubstitute.Arg.Any<System.Threading.CancellationToken>()),
            _ => Task.FromException<TicketFlowVersion>(new TicketFlowSaveConflictException()));
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var svc = new TicketFlowService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<TicketFlowService>.Instance);

        var result = await svc.SaveAsync("t1", null, TestOff(), Admin);

        result.Saved.ShouldBeFalse();
        result.Problems.ShouldHaveSingleItem().Message.ShouldContain("at the same moment");
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task NothingSaved_RunsTheBuiltInFlow(string store)
    {
        var flow = await Service(store).ResolveAsync("t1", Guid.NewGuid());

        flow.Source.ShouldBe(TicketFlowSource.BuiltIn);
        flow.Version.ShouldBe(0);
        flow.Definition.Summary().ShouldBe("Code → Checks → Review → Test → Merge");
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Save_AppendsVersions_WithWhoAndWhen_AndRevertBringsOneBack(string store)
    {
        var svc = Service(store);
        var changed = 0;
        svc.Changed += _ => changed++;

        var v1 = await svc.SaveAsync("t1", null, NoReviewForChores(), Admin);
        var v2 = await svc.SaveAsync("t1", null, TestOff(), Admin);
        v1.Saved.ShouldBeTrue();
        v2.Version!.Version.ShouldBe(2);
        (await svc.ResolveAsync("t1", null)).Definition.Find("test")!.Enabled.ShouldBeFalse();

        var reverted = await svc.RevertAsync("t1", null, 1, Admin);

        reverted.Version!.Version.ShouldBe(3);
        reverted.Version.Note.ShouldBe("Reverted to v1");
        var now = await svc.ResolveAsync("t1", null);
        now.Version.ShouldBe(3);
        now.Source.ShouldBe(TicketFlowSource.Tenant);
        now.Definition.Find("review")!.Skip.ShouldBe(["type:chore"]);
        var history = await svc.HistoryAsync("t1", null);
        history.Select(h => h.Version).ShouldBe([3, 2, 1]);
        history.ShouldAllBe(h => h.CreatedByName == "Ada Admin" && h.CreatedByUserId == "u-admin");
        changed.ShouldBe(3);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task InvalidFlow_IsRefusedOnSave_WithTheReason_AndNothingIsStored(string store)
    {
        var svc = Service(store);
        var bothOff = TestOff().With(TicketFlowDefinition.Default.Find("review")! with { Enabled = false });

        var result = await svc.SaveAsync("t1", null, bothOff, Admin);

        result.Saved.ShouldBeFalse();
        result.Reason.ShouldContain("Turn on Review or Test");
        (await svc.HistoryAsync("t1", null)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AMember_CantChangeTheFlow(string store)
    {
        var svc = Service(store);
        (await svc.SaveAsync("t1", null, TestOff(), Member)).Reason.ShouldContain("Only an admin");
        await svc.SaveAsync("t1", null, TestOff(), Admin);
        (await svc.RevertAsync("t1", null, 1, Member)).Saved.ShouldBeFalse();
        (await svc.HistoryAsync("t1", null)).Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheBoardsOwnFlow_Wins_AndUsingTheDefaultAgainFallsBack(string store)
    {
        var svc = Service(store);
        var board = Guid.NewGuid();
        var other = Guid.NewGuid();
        await svc.SaveAsync("t1", null, NoReviewForChores(), Admin);
        await svc.SaveAsync("t1", board, TestOff(), Admin);

        (await svc.ResolveAsync("t1", board)).Source.ShouldBe(TicketFlowSource.Board);
        (await svc.ResolveAsync("t1", board)).Definition.Find("test")!.Enabled.ShouldBeFalse();
        (await svc.ResolveAsync("t1", other)).Source.ShouldBe(TicketFlowSource.Tenant);

        await svc.UseTenantFlowAsync("t1", board, Admin);

        var back = await svc.ResolveAsync("t1", board);
        back.Source.ShouldBe(TicketFlowSource.Tenant);
        back.Version.ShouldBe(1);
        (await svc.RevertAsync("t1", board, 1, Admin)).Reason.ShouldContain("isn't one of this flow's versions"); // v1 is the tenant's
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TwoTenants_NeverSeeEachOthersFlows(string store)
    {
        var svc = Service(store);
        await svc.SaveAsync("tenant-a", null, TestOff(), Admin);

        (await svc.ResolveAsync("tenant-b", null)).Source.ShouldBe(TicketFlowSource.BuiltIn);
        (await svc.HistoryAsync("tenant-b", null)).ShouldBeEmpty();
        (await svc.ForTaskAsync("tenant-b", Guid.NewGuid(), 1)).Refused!.ShouldContain("no longer exists");
        (await svc.RevertAsync("tenant-b", null, 1, Admin)).Saved.ShouldBeFalse();
        // Each tenant numbers its own versions.
        (await svc.SaveAsync("tenant-b", null, NoReviewForChores(), Admin)).Version!.Version.ShouldBe(1);
        (await svc.ResolveAsync("tenant-a", null)).Definition.Find("test")!.Enabled.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ATask_KeepsTheVersionItStartedWith(string store)
    {
        var svc = Service(store);
        var board = Guid.NewGuid();
        var started = (await svc.SaveAsync("t1", null, TestOff(), Admin)).Version!.Version;
        await svc.SaveAsync("t1", null, NoReviewForChores(), Admin);

        var flow = await svc.ForTaskAsync("t1", board, started);
        flow.Version.ShouldBe(started);
        flow.Definition.Find("test")!.Enabled.ShouldBeFalse();
        (await svc.ForTaskAsync("t1", board, started)).Version.ShouldBe(started); // cached path, same answer
        (await svc.ForTaskAsync("t1", board, 0)).Source.ShouldBe(TicketFlowSource.BuiltIn);
        (await svc.ForTaskAsync("t1", board, null)).Version.ShouldBe(2); // a task from before ticket flows: the current one
    }

    [Fact]
    public async Task AStoredFlowThatBreaksARule_IsRefusedAtRunTime_AndTheBuiltInFlowRuns()
    {
        var services = new ServiceCollection();
        var store = new InMemoryTicketFlowStore();
        services.AddSingleton<ITicketFlowStore>(store);
        var svc = new TicketFlowService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<TicketFlowService>.Instance);
        var broken = TicketFlowDefinition.Chain(TicketFlowDefinition.Default.Nodes.Where(n => n.Kind != TicketStepKind.Checks).ToList());
        await store.AppendAsync("t1", Guid.Empty, broken.ToJson(), "u", "u", null); // written behind the validator's back

        var flow = await svc.ResolveAsync("t1", null);

        flow.Source.ShouldBe(TicketFlowSource.BuiltIn);
        flow.Definition.ShouldBeSameAs(TicketFlowDefinition.Default);
        flow.Refused!.ShouldContain("v1 was refused (Checks must run before Merge");
        (await svc.ForTaskAsync("t1", Guid.NewGuid(), 1)).Refused.ShouldNotBeNull();
        (await svc.RevertAsync("t1", null, 1, Admin)).Saved.ShouldBeFalse();
    }
}
