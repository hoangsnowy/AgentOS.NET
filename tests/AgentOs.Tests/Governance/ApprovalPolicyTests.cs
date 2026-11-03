// Approval policies (E1.5), pure: which gates a policy requires, the per-project lines that replace the tenant policy,
// and the FAIL-CLOSED evaluation — an approval is never assumed (not asked → ask, asked → wait, rejected → stop, nobody
// can approve → stop), and only the approver or admin role may decide.

using System.Collections.Generic;
using AgentOs.Domain.Governance;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Governance;

public sealed class ApprovalPolicyTests
{
    private static readonly Guid Session = Guid.NewGuid();

    private static ApprovalRequest Request(ApprovalGate gate, ApprovalStatus status = ApprovalStatus.Pending, string? by = null, string? note = null) =>
        ApprovalRequest.New("t1", Guid.NewGuid(), Session, 12, gate, 0, "#12 Add dark mode", null, "Autopilot", DateTimeOffset.UtcNow) with
        {
            Status = status,
            DecidedBy = by,
            DecidedAtUtc = status == ApprovalStatus.Pending ? null : DateTimeOffset.UtcNow,
            Note = note,
        };

    [Fact]
    public void None_RequiresNothing_AndOpensReadyPullRequests()
    {
        foreach (var gate in ApprovalPolicy.InOrder) { ApprovalPolicy.None.Requires(gate).ShouldBeFalse(); }
        ApprovalPolicy.None.DraftPullRequests.ShouldBeFalse();
        ApprovalPolicy.None.Summary.ShouldBe("no approvals");
    }

    [Fact]
    public void Requires_OnlyTheGatesItNames()
    {
        var policy = new ApprovalPolicy(ApprovalGates.Plan | ApprovalGates.Merge, DraftPullRequests: true);

        policy.Requires(ApprovalGate.Plan).ShouldBeTrue();
        policy.Requires(ApprovalGate.Merge).ShouldBeTrue();
        policy.Requires(ApprovalGate.Diff).ShouldBeFalse();
        policy.Requires(ApprovalGate.PullRequest).ShouldBeFalse();
        policy.Summary.ShouldBe("approve plan, merge · draft pull requests");
    }

    [Theory]
    [InlineData("plan, pr, diff, merge, draft", ApprovalGates.Plan | ApprovalGates.PullRequest | ApprovalGates.Diff | ApprovalGates.Merge, true)]
    [InlineData("MERGE", ApprovalGates.Merge, false)]
    [InlineData("pull-request; draft", ApprovalGates.PullRequest, true)]
    [InlineData("none", ApprovalGates.None, false)]
    [InlineData("", ApprovalGates.None, false)]
    public void ParseGates_ReadsTheWords(string list, ApprovalGates gates, bool draft)
    {
        var (policy, error) = ApprovalPolicy.ParseGates(list);

        error.ShouldBeNull();
        policy.ShouldBe(new ApprovalPolicy(gates, draft));
    }

    [Theory]
    [InlineData("plan, deploy")]
    [InlineData("none, merge")]
    public void ParseGates_RefusesWhatItDoesNotKnow(string list)
    {
        var (policy, error) = ApprovalPolicy.ParseGates(list);

        policy.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Resolve_AProjectLineReplacesTheTenantPolicy_ByNameOrId()
    {
        var tenant = new ApprovalPolicy(ApprovalGates.Merge, false);
        var id = Guid.NewGuid();
        var lines = $"# per project\nWeb-Shop: plan, merge, draft\n{id}: none\n";

        ApprovalPolicy.Resolve(tenant, lines, "web-shop", Guid.NewGuid()).ShouldBe(new ApprovalPolicy(ApprovalGates.Plan | ApprovalGates.Merge, true));
        ApprovalPolicy.Resolve(tenant, lines, "docs", id).ShouldBe(new ApprovalPolicy(ApprovalGates.None, false));
        ApprovalPolicy.Resolve(tenant, lines, "other", Guid.NewGuid()).ShouldBe(tenant);
        ApprovalPolicy.Resolve(tenant, null, "web-shop", id).ShouldBe(tenant);
    }

    [Fact]
    public void ValidateOverrides_NamesTheBadLine()
    {
        ApprovalPolicy.ValidateOverrides("web: plan\napi: merge, draft").ShouldBeNull();
        ApprovalPolicy.ValidateOverrides(null).ShouldBeNull();
        ApprovalPolicy.ValidateOverrides("web plan").ShouldNotBeNull().ShouldContain("project: plan, merge, draft");
        ApprovalPolicy.ValidateOverrides("api: merge, ship").ShouldNotBeNull().ShouldStartWith("api:");
        ApprovalPolicy.ValidateOverrides(new string('x', 4001)).ShouldNotBeNull();
    }

    [Fact]
    public void Evaluate_NotRequired_Proceeds_WhateverIsStored()
    {
        ApprovalRules.Evaluate(ApprovalPolicy.None, ApprovalGate.Merge, null, approvers: 0).MayProceed.ShouldBeTrue();
        ApprovalRules.Evaluate(ApprovalPolicy.None, ApprovalGate.Merge, Request(ApprovalGate.Merge, ApprovalStatus.Rejected, "bob"), 0)
            .MayProceed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_Required_NeverAsked_Asks_EvenWhenTheApproverCountIsUnknown()
    {
        var policy = new ApprovalPolicy(ApprovalGates.Diff, false);

        var check = ApprovalRules.Evaluate(policy, ApprovalGate.Diff, null, approvers: null);

        check.Verdict.ShouldBe(ApprovalVerdict.Request);
        check.MayProceed.ShouldBeFalse();
        check.Stops.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_Asked_Waits()
    {
        var policy = new ApprovalPolicy(ApprovalGates.Merge, false);

        var check = ApprovalRules.Evaluate(policy, ApprovalGate.Merge, Request(ApprovalGate.Merge), approvers: 2);

        check.Verdict.ShouldBe(ApprovalVerdict.Wait);
        check.MayProceed.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_Approved_Proceeds_AndCarriesWhoApproved()
    {
        var policy = new ApprovalPolicy(ApprovalGates.Merge, false);

        var check = ApprovalRules.Evaluate(policy, ApprovalGate.Merge, Request(ApprovalGate.Merge, ApprovalStatus.Approved, "alice"), approvers: 0);

        check.MayProceed.ShouldBeTrue();
        check.Request!.DecidedBy.ShouldBe("alice");
    }

    [Fact]
    public void Evaluate_Rejected_Stops_WithTheApproversReason()
    {
        var policy = new ApprovalPolicy(ApprovalGates.Plan, false);

        var check = ApprovalRules.Evaluate(policy, ApprovalGate.Plan, Request(ApprovalGate.Plan, ApprovalStatus.Rejected, "bob", "not this sprint"), 3);

        check.Verdict.ShouldBe(ApprovalVerdict.Rejected);
        check.Stops.ShouldBeTrue();
        check.Reason.ShouldBe("The plan was rejected by bob: not this sprint");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_NobodyCanApprove_FailsClosed_NeverProceeds(bool alreadyAsked)
    {
        var policy = new ApprovalPolicy(ApprovalGates.Merge, false);

        var check = ApprovalRules.Evaluate(policy, ApprovalGate.Merge, alreadyAsked ? Request(ApprovalGate.Merge) : null, approvers: 0);

        check.Verdict.ShouldBe(ApprovalVerdict.NoApprover);
        check.Stops.ShouldBeTrue();
        check.MayProceed.ShouldBeFalse();
        check.Reason.ShouldNotBeNull().ShouldContain("nobody in this workspace holds the approver or admin role");
    }

    [Fact]
    public void Evaluate_ARequestForAnotherGate_IsAProgrammingError()
    {
        var policy = new ApprovalPolicy(ApprovalGates.Merge | ApprovalGates.Diff, false);

        Should.Throw<ArgumentException>(() => ApprovalRules.Evaluate(policy, ApprovalGate.Merge, Request(ApprovalGate.Diff, ApprovalStatus.Approved, "a"), 1));
    }

    [Theory]
    [InlineData(new[] { "approver" }, true)]
    [InlineData(new[] { "admin" }, true)]
    [InlineData(new[] { "member", "approver" }, true)]
    [InlineData(new[] { "member" }, false)]
    [InlineData(new[] { "platform-admin" }, false)]
    [InlineData(new string[0], false)]
    public void CanDecide_OnlyApproverOrAdmin(string[] roles, bool expected) =>
        ApprovalRules.CanDecide(roles).ShouldBe(expected);

    [Fact]
    public void RunFor_PlanOncePerTask_OtherGatesPerFixRound()
    {
        ApprovalRequest.RunFor(ApprovalGate.Plan, 3).ShouldBe(0);
        ApprovalRequest.RunFor(ApprovalGate.Diff, 3).ShouldBe(3);
        ApprovalRequest.RunFor(ApprovalGate.Merge, -1).ShouldBe(0);
        ApprovalRequest.New("t", Guid.NewGuid(), Guid.NewGuid(), 1, ApprovalGate.Plan, 5, "x", null, "Autopilot", DateTimeOffset.UtcNow).Run.ShouldBe(0);
    }

    [Fact]
    public void RejectionReason_WithoutANote_StillSaysWho()
    {
        Request(ApprovalGate.PullRequest, ApprovalStatus.Rejected, "carol").RejectionReason.ShouldBe("Opening the pull request was rejected by carol.");
    }

    [Fact]
    public void Gates_InOrder_ListsEveryGateOnce()
    {
        new HashSet<ApprovalGate>(ApprovalPolicy.InOrder).Count.ShouldBe(Enum.GetValues<ApprovalGate>().Length);
    }
}
