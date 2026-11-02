// Design gate (ADR-0016) — no blind approvals, through the Board service a person's click goes through. A real owner
// approved two tickets on Board › Tasks while their reviews were still Pending (no screenshots: the Testing agent hadn't
// run), and those approvals passed the gate. Now: a decision on a review without screenshots is refused in plain words
// (the service and the store both check), and when a test run sends screenshots back, an earlier approval is cleared,
// audited and said on the task's feed, so the owner decides on what they can see. Approve-after-screenshots is unchanged.

using System.Collections.Concurrent;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Pipeline.Pipeline;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.Web.Services;
using AgentOs.Web.Services.BoardTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class DesignDecisionTests
{
    private const string Tenant = "t1";
    private static readonly Guid Board = Guid.NewGuid();
    private static readonly DesignShot[] Shots = AgentOs.Tests.Sessions.DesignReviewStoreTests.Shots;

    private sealed class RecordingAudit : IAuditLog
    {
        public ConcurrentQueue<AuditEntry> Entries { get; } = new();

        public Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
        {
            Entries.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> ListAsync(string tenantId, int max = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>(Entries.Where(e => e.TenantId == tenantId).ToList());
    }

    // The Testing agent: passes, with the screenshots it took.
    private sealed class PassingTester(IReadOnlyList<DesignShot> shots) : IAcceptanceAgent
    {
        public Task<AcceptanceVerdict> RunAsync(AcceptanceRequest request, CancellationToken ct = default) =>
            Task.FromResult(new AcceptanceVerdict(true, true, [], "All criteria pass.", null) { Screenshots = shots });
    }

    private sealed record Rig(BoardTaskService Service, ISessionRepository Sessions, InMemoryDesignReviewStore Reviews, RecordingAudit Audit,
        ISessionRunFeed Feed, RemoteSessionEntity Session);

    private static Rig Build(IAcceptanceAgent? tester = null)
    {
        var sessions = Substitute.For<ISessionRepository>();
        var boards = Substitute.For<IWorkspaceRepository>();
        var config = Substitute.For<IAppConfigStore>();
        var reviews = new InMemoryDesignReviewStore();
        var audit = new RecordingAudit();
        var session = new RemoteSessionEntity { Id = Guid.NewGuid(), TenantId = Tenant, WorkspaceId = Board, Title = "Checkout", IssueNumber = 122, MemberUserId = "u1" };
        sessions.ListForTenantAsync(Tenant, Arg.Any<CancellationToken>()).Returns([session]);
        boards.GetForTenantAsync(Tenant, Board, Arg.Any<CancellationToken>()).Returns(new WorkspaceEntity { Id = Board, TenantId = Tenant, CredentialRef = "k" });
        config.GetForTenantAsync(Tenant, "k", Arg.Any<CancellationToken>()).Returns("pat");
        sessions.ListReposForTenantAsync(Tenant, session.Id, Arg.Any<CancellationToken>()).Returns([new SessionRepoEntity
        {
            Id = Guid.NewGuid(), TenantId = Tenant, SessionId = session.Id, Owner = "acme", Repo = "shop", Status = "Done",
            BranchName = "agentos/122", PrUrl = "https://github.com/acme/shop/pull/12",
        }]);
        var services = new ServiceCollection()
            .AddSingleton(sessions).AddSingleton(boards).AddSingleton(config)
            .AddSingleton<IDesignReviewStore>(reviews).AddSingleton<IAuditLog>(audit)
            .AddSingleton(Substitute.For<IPrCreationService>())
            .AddSingleton(tester ?? new PassingTester(Shots))
            .BuildServiceProvider();
        var feed = Substitute.For<ISessionRunFeed>();
        var service = new BoardTaskService(services.GetRequiredService<IServiceScopeFactory>(), feed, new TaskStopRegistry(),
            Options.Create(new PipelineOptions()));
        return new(service, sessions, reviews, audit, feed, session);
    }

    // ── Refused: nothing to look at ──

    [Fact]
    public async Task DecideDesignAsync_NoReviewYet_IsRefused_AndCreatesNoReview()
    {
        var rig = Build();

        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            rig.Service.DecideDesignAsync(Tenant, rig.Session.Id, DesignDecision.Approved, null, "Ann"));

        refused.Message.ShouldBe(DesignReview.NothingToDecideYet);
        (await rig.Reviews.ListForTaskAsync(Tenant, rig.Session.Id)).ShouldBeEmpty();
        rig.Audit.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(DesignDecision.Approved, null)]
    [InlineData(DesignDecision.ChangesAsked, "Stack the form on the phone.")]
    public async Task DecideDesignAsync_PendingReviewWithoutScreenshots_IsRefused(DesignDecision decision, string? note)
    {
        var rig = Build();
        await rig.Reviews.EnsureAsync(DesignReview.New(Tenant, Board, rig.Session.Id, 122, 0, DesignReviewStatus.Pending));

        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            rig.Service.DecideDesignAsync(Tenant, rig.Session.Id, decision, note, "Ann"));

        refused.Message.ShouldContain("Testing agent takes the screenshots during Test");
        var review = (await rig.Reviews.GetAsync(Tenant, rig.Session.Id, 0))!;
        review.Decision.ShouldBeNull();
        review.Gate.ShouldNotBe(DesignGateState.Passed);
        rig.Audit.Entries.ShouldBeEmpty();
    }

    // The page shows a copy without screenshots; screenshots landed since — the person still hasn't seen them: refused.
    [Fact]
    public async Task DecideAsync_FromACopyWithoutScreenshots_IsRefused_EvenIfTheyLandedSince()
    {
        var rig = Build();
        var seen = await rig.Reviews.EnsureAsync(DesignReview.New(Tenant, Board, rig.Session.Id, 122, 0, DesignReviewStatus.Pending));
        await rig.Reviews.SaveVerdictAsync(Tenant, rig.Session.Id, 0, new DesignVerdictUpdate(DesignReviewStatus.Pending, Shots: Shots, NewCapture: true));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            BoardTaskService.DecideAsync(rig.Reviews, seen, DesignDecision.Approved, null, "Ann", rig.Audit));

        (await rig.Reviews.GetAsync(Tenant, rig.Session.Id, 0))!.Decision.ShouldBeNull();
    }

    // ── Allowed, unchanged: the owner looked at the screenshots ──

    [Fact]
    public async Task DecideDesignAsync_WithScreenshots_Approves_PassesTheGate_AndIsAudited()
    {
        var rig = Build();
        await rig.Reviews.EnsureAsync(DesignReview.New(Tenant, Board, rig.Session.Id, 122, 0, DesignReviewStatus.Changes) with { Shots = Shots });

        var stored = await rig.Service.DecideDesignAsync(Tenant, rig.Session.Id, DesignDecision.Approved, null, "Ann");

        stored!.Decision.ShouldBe(DesignDecision.Approved);
        stored.Gate.ShouldBe(DesignGateState.Passed);                      // the owner's decision wins over the agent's "changes"
        var entry = rig.Audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe(AuditActions.DesignDecided);
        entry.UserId.ShouldBe("Ann");
        entry.Target.ShouldNotBeNull().ShouldContain("#122");
        entry.Target.ShouldContain("approved");
    }

    // ── New screenshots clear an earlier approval ──

    // The lab's case: Pending + Approved with no screenshots. Not Passed while it waits; when the Testing agent's
    // screenshots arrive the approval is cleared, audited and said on the feed — the owner decides on what they can see.
    [Fact]
    public async Task TestRun_DeliveringScreenshots_ClearsABlindApproval_AuditsIt_AndTheOwnerDecidesAgain()
    {
        var rig = Build();
        var blind = await rig.Reviews.EnsureAsync(DesignReview.New(Tenant, Board, rig.Session.Id, 122, 0, DesignReviewStatus.Pending)
            with { Decision = DesignDecision.Approved, DecidedBy = "owner", DecidedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) });
        blind.Gate.ShouldBe(DesignGateState.NotChecked);                   // never Passed

        var run = await rig.Service.BeginTestAsync(Tenant, rig.Session.Id, captureDesign: true);
        run.Status.ShouldBe(TaskStart.Started);
        await run.Completion;

        var after = (await rig.Reviews.GetAsync(Tenant, rig.Session.Id, 0))!;
        after.Shots.Count.ShouldBe(Shots.Length);
        after.Decision.ShouldBeNull();
        after.DecidedBy.ShouldBeNull();
        after.Gate.ShouldBe(DesignGateState.NotChecked);                   // waits for the Designer (or the owner) again
        var cleared = rig.Audit.Entries.ShouldHaveSingleItem();
        cleared.Action.ShouldBe(AuditActions.DesignApprovalCleared);
        cleared.Target.ShouldNotBeNull().ShouldContain("#122");
        cleared.Target.ShouldContain("owner");
        rig.Feed.Received().Publish(Arg.Is<SessionRunEvent>(e => e.Message.Contains("earlier design approval was cleared", StringComparison.Ordinal)));

        // Now the owner can see them: approving works, and passes the gate.
        (await rig.Service.DecideDesignAsync(Tenant, rig.Session.Id, DesignDecision.Approved, null, "Ann"))!.Gate.ShouldBe(DesignGateState.Passed);
    }

    [Fact]
    public async Task TestRun_DeliveringScreenshots_KeepsAnAskForChanges_AndAuditsNothing()
    {
        var rig = Build();
        await rig.Reviews.EnsureAsync(DesignReview.New(Tenant, Board, rig.Session.Id, 122, 0, DesignReviewStatus.Changes) with { Shots = Shots });
        await rig.Service.DecideDesignAsync(Tenant, rig.Session.Id, DesignDecision.ChangesAsked, "Stack the form on the phone.", "Ann");

        var run = await rig.Service.BeginTestAsync(Tenant, rig.Session.Id, captureDesign: true);
        await run.Completion;

        var after = (await rig.Reviews.GetAsync(Tenant, rig.Session.Id, 0))!;
        after.Decision.ShouldBe(DesignDecision.ChangesAsked);
        after.Gate.ShouldBe(DesignGateState.ChangesRequested);
        rig.Audit.Entries.Select(e => e.Action).ShouldBe([AuditActions.DesignDecided]);
    }
}
