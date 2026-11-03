// Approval requests end to end below the UI (E1.5): the store asks once per (task, gate, change) and never lets a decision
// be overwritten or cross tenants; only the approver or admin role decides; a rejection needs a reason and stops the task
// (its open changes blocked with it); every decision is audited (who, when, what); the policy is read from the settings
// with the project line winning; an approver is a member too; and the approver count fails closed on a known zero.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Governance;
using AgentOs.Modules.Integration.Governance;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Approvals;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.Web.Services.Approvals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Governance;

public sealed class ApprovalDecisionTests
{
    private static ApprovalRequest Ask(string tenant, Guid session, ApprovalGate gate = ApprovalGate.Merge, int run = 0) =>
        ApprovalRequest.New(tenant, Guid.NewGuid(), session, 12, gate, run, "#12 Add dark mode", "https://github.com/o/r/pull/3", "Autopilot", DateTimeOffset.UtcNow);

    // ── The store ──

    [Fact]
    public async Task Store_AsksOncePerTaskGateAndChange()
    {
        var store = new InMemoryApprovalStore();
        var session = Guid.NewGuid();

        var first = await store.RequestAsync(Ask("t1", session));
        var again = await store.RequestAsync(Ask("t1", session));
        var nextRound = await store.RequestAsync(Ask("t1", session, run: 1));

        again.Id.ShouldBe(first.Id);
        nextRound.Id.ShouldNotBe(first.Id);
        (await store.ListRecentAsync("t1")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Store_NeverStoresAPreApprovedRequest()
    {
        var store = new InMemoryApprovalStore();

        var stored = await store.RequestAsync(Ask("t1", Guid.NewGuid()) with { Status = ApprovalStatus.Approved, DecidedBy = "mallory" });

        stored.Status.ShouldBe(ApprovalStatus.Pending);
        stored.DecidedBy.ShouldBeNull();
    }

    [Fact]
    public async Task Store_ADecisionIsNeverOverwritten()
    {
        var store = new InMemoryApprovalStore();
        var asked = await store.RequestAsync(Ask("t1", Guid.NewGuid()));

        var approved = await store.DecideAsync("t1", asked.Id, new ApprovalDecision(true, "alice", null, DateTimeOffset.UtcNow));
        var flipped = await store.DecideAsync("t1", asked.Id, new ApprovalDecision(false, "bob", "no", DateTimeOffset.UtcNow));

        approved!.Status.ShouldBe(ApprovalStatus.Approved);
        flipped!.Status.ShouldBe(ApprovalStatus.Approved);
        flipped.DecidedBy.ShouldBe("alice");
    }

    [Fact]
    public async Task Store_IsTenantScoped()
    {
        var store = new InMemoryApprovalStore();
        var asked = await store.RequestAsync(Ask("t1", Guid.NewGuid()));

        (await store.FindAsync("t2", asked.Id)).ShouldBeNull();
        (await store.DecideAsync("t2", asked.Id, new ApprovalDecision(true, "eve", null, DateTimeOffset.UtcNow))).ShouldBeNull();
        (await store.ListRecentAsync("t2")).ShouldBeEmpty();
        (await store.FindAsync("t1", asked.Id))!.Status.ShouldBe(ApprovalStatus.Pending);
    }

    // ── Deciding on Board › Tasks ──

    private sealed class Harness
    {
        public InMemoryApprovalStore Store { get; } = new();
        public IAuditLog Audit { get; } = Substitute.For<IAuditLog>();
        public ISessionRepository Sessions { get; } = Substitute.For<ISessionRepository>();
        public ApprovalGateService Service { get; }

        public Harness()
        {
            var sp = new ServiceCollection()
                .AddSingleton<IApprovalStore>(Store)
                .AddSingleton(Audit)
                .AddSingleton(Sessions)
                .BuildServiceProvider();
            Service = new ApprovalGateService(sp.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<ApprovalGateService>.Instance);
        }
    }

    private static readonly ApprovalActor Approver = new("u-alice", "alice", CanApprove: true);
    private static readonly ApprovalActor Member = new("u-bob", "bob", CanApprove: false);

    [Fact]
    public async Task Decide_AMemberWithoutTheRole_IsRefused_AndNothingIsRecorded()
    {
        var h = new Harness();
        var asked = await h.Store.RequestAsync(Ask("t1", Guid.NewGuid()));

        var outcome = await h.Service.DecideAsync("t1", asked.Id, approve: true, null, Member);

        outcome.Ok.ShouldBeFalse();
        outcome.Error.ShouldNotBeNull().ShouldContain("approver or an admin");
        (await h.Store.FindAsync("t1", asked.Id))!.Status.ShouldBe(ApprovalStatus.Pending);
        await h.Audit.DidNotReceiveWithAnyArgs().WriteAsync(default!);
    }

    [Fact]
    public async Task Decide_Approve_IsRecordedWithWhoAndWhen_AndAudited()
    {
        var h = new Harness();
        var asked = await h.Store.RequestAsync(Ask("t1", Guid.NewGuid()));
        var notified = new List<string>();
        h.Service.Changed += notified.Add;

        var outcome = await h.Service.DecideAsync("t1", asked.Id, approve: true, null, Approver);

        outcome.Ok.ShouldBeTrue();
        outcome.Request!.Status.ShouldBe(ApprovalStatus.Approved);
        outcome.Request.DecidedBy.ShouldBe("alice");
        outcome.Request.DecidedAtUtc.ShouldNotBeNull();
        await h.Audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e =>
            e.TenantId == "t1" && e.UserId == "u-alice" && e.Action == AuditActions.ApprovalDecided && e.Target!.StartsWith("#12 merge approved", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        notified.ShouldBe(["t1"]);
    }

    [Fact]
    public async Task Decide_Reject_NeedsAReason()
    {
        var h = new Harness();
        var asked = await h.Store.RequestAsync(Ask("t1", Guid.NewGuid()));

        (await h.Service.DecideAsync("t1", asked.Id, approve: false, "  ", Approver)).Ok.ShouldBeFalse();
        (await h.Store.FindAsync("t1", asked.Id))!.Status.ShouldBe(ApprovalStatus.Pending);
    }

    [Fact]
    public async Task Decide_Reject_BlocksTheTasksOpenChanges_WithTheReason()
    {
        var h = new Harness();
        var session = Guid.NewGuid();
        var open = new SessionRepoEntity { Id = Guid.NewGuid(), TenantId = "t1", SessionId = session, Status = "Done", BranchName = "agentos/12", PrUrl = "https://github.com/o/r/pull/3" };
        var merged = new SessionRepoEntity { Id = Guid.NewGuid(), TenantId = "t1", SessionId = session, Status = "Merged" };
        h.Sessions.ListReposForTenantAsync("t1", session, Arg.Any<CancellationToken>()).Returns([open, merged]);
        var asked = await h.Store.RequestAsync(Ask("t1", session));

        var outcome = await h.Service.DecideAsync("t1", asked.Id, approve: false, "the migration drops a column", Approver);

        outcome.Ok.ShouldBeTrue();
        outcome.Request!.Status.ShouldBe(ApprovalStatus.Rejected);
        await h.Sessions.Received(1).UpdateRepoRunResultAsync("t1", open.Id, "Blocked", "agentos/12", open.PrUrl,
            "The merge was rejected by alice: the migration drops a column", Arg.Any<CancellationToken>());
        await h.Sessions.DidNotReceive().UpdateRepoRunResultAsync("t1", merged.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await h.Sessions.Received(1).RecomputeSessionStatusForTenantAsync("t1", session, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decide_Twice_TheSecondIsRefused()
    {
        var h = new Harness();
        var asked = await h.Store.RequestAsync(Ask("t1", Guid.NewGuid()));
        await h.Service.DecideAsync("t1", asked.Id, approve: true, null, Approver);

        var second = await h.Service.DecideAsync("t1", asked.Id, approve: false, "changed my mind", new ApprovalActor("u-carol", "carol", true));

        second.Ok.ShouldBeFalse();
        second.Error.ShouldNotBeNull().ShouldContain("Already approved by alice");
    }

    [Fact]
    public void AuditTarget_ClipsTheNote()
    {
        var r = Ask("t1", Guid.NewGuid()) with { Status = ApprovalStatus.Rejected, Note = new string('n', 500) };

        ApprovalGateService.AuditTarget(r).Length.ShouldBeLessThan(400);
    }

    [Fact]
    public void CompareLink_KeepsTheBranchsSlashes() =>
        ApprovalGateService.CompareLink(AgentOs.Domain.Workspaces.SourceProviderKind.GitHub, "o", "r", "main", "agentos/issue 12")
            .ShouldBe("https://github.com/o/r/compare/main...agentos/issue%2012");

    // ── The policy from the settings ──

    private sealed class FakeSettings(Dictionary<string, string> values) : ISettings
    {
        public long Version => 0;

        public ValueTask<SettingValue> GetAsync(string key, string? tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SettingValue(tenantId == "t1" ? values.GetValueOrDefault(key) : null, SettingSource.Tenant));

        public ValueTask<IReadOnlyList<SettingView>> ListAsync(string? tenantId, string? category = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SettingWriteResult> SetAsync(string key, string? value, SettingScope scope, string? tenantId, string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ReadPolicy_TenantGates_AndTheProjectLineWins()
    {
        var settings = new FakeSettings(new()
        {
            [ApprovalSettings.MergeKey] = "true",
            [ApprovalSettings.DraftPullRequestsKey] = "true",
            [ApprovalSettings.ProjectsKey] = "docs: none",
        });

        (await ApprovalSettings.ReadPolicyAsync(settings, "t1", "shop", Guid.NewGuid())).ShouldBe(new ApprovalPolicy(ApprovalGates.Merge, true));
        (await ApprovalSettings.ReadPolicyAsync(settings, "t1", "docs", Guid.NewGuid())).ShouldBe(ApprovalPolicy.None);
        (await ApprovalSettings.ReadPolicyAsync(settings, "t2", "shop", Guid.NewGuid())).ShouldBe(ApprovalPolicy.None);
    }

    [Fact]
    public void Settings_AreDeclared_WithSafeDefaults_AndAValidator()
    {
        var all = new ApprovalSettings().Describe().ToList();

        all.Select(d => d.Key).ShouldBe(ApprovalSettings.Keys);
        all.Where(d => d.Kind == SettingKind.Boolean).ShouldAllBe(d => d.DefaultValue == "false");
        all.Single(d => d.Key == ApprovalSettings.ProjectsKey).Validator!("web plan").ShouldNotBeNull();
    }

    // ── Roles and who can approve ──

    [Fact]
    public void MemberRoles_AnApproverStaysAnApprover_UnderRbacV2()
    {
        // RBAC v2 (E1.2): approver alone already reads everything (Member policy = ViewWork) — it is no longer expanded
        // into member, which would hand it RunAgents + ManageWorkspaces (separation of duties).
        MemberRoles.Sanitize(["approver"]).ShouldBe(["approver"]);
        MemberRoles.Sanitize(["admin", "approver"]).ShouldBe(["admin", "approver"]);
        MemberRoles.Sanitize(["root", "member"]).ShouldBe(["member"]);
        MemberRoles.Primary(["member", "approver"]).ShouldBe("approver");
        MemberRoles.Primary(["admin", "approver", "member"]).ShouldBe("admin");
        MemberRoles.Primary([]).ShouldBeNull();
    }

    private static IApproverDirectory Directory(IKeycloakAdminClient keycloak)
    {
        var sp = new ServiceCollection().AddSingleton(keycloak).BuildServiceProvider();
        return new KeycloakApproverDirectory(sp.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<KeycloakApproverDirectory>.Instance);
    }

    [Fact]
    public async Task ApproverDirectory_CountsEnabledApproversAndAdmins()
    {
        var keycloak = Substitute.For<IKeycloakAdminClient>();
        keycloak.ListUsersByTenantAsync("t1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new KeycloakUser("1", "alice", null, true, true, ["member", "approver"]),
            new KeycloakUser("2", "root", null, true, true, ["admin"]),
            new KeycloakUser("3", "bob", null, true, true, ["member"]),
            new KeycloakUser("4", "gone", null, false, true, ["approver"]),
        ]);

        (await Directory(keycloak).CountApproversAsync("t1")).ShouldBe(2);
    }

    [Fact]
    public async Task ApproverDirectory_KnownZero_IsZero_SoGatesFailClosed()
    {
        var keycloak = Substitute.For<IKeycloakAdminClient>();
        keycloak.ListUsersByTenantAsync("t1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([new KeycloakUser("3", "bob", null, true, true, ["member"])]);

        (await Directory(keycloak).CountApproversAsync("t1")).ShouldBe(0);
    }

    [Fact]
    public async Task ApproverDirectory_WithoutKeycloak_OrOnFailure_IsUnknown()
    {
        (await Directory(new NullKeycloakAdminClient()).CountApproversAsync("t1")).ShouldBeNull();

        var failing = Substitute.For<IKeycloakAdminClient>();
        failing.ListUsersByTenantAsync("t1", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<KeycloakUser>>>(_ => throw new InvalidOperationException("Keycloak down"));
        (await Directory(failing).CountApproversAsync("t1")).ShouldBeNull();
    }
}
