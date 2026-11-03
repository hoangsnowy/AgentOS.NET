// AgentOs.Domain/Governance/Approvals.cs
// Approval requests (E1.5): one per (task, gate, change). Autopilot (or a Board run) asks; a person with the approver or
// admin role decides on Board › Tasks — Approve or Reject, recorded with who and when (and audited). The evaluation is
// FAIL CLOSED: an approval is never assumed. No record → ask; asked → wait; rejected → stop; and when the tenant is known
// to have nobody who could approve, the step stops with that reason instead of waiting forever or going ahead.
// An approval stands for the change it was given on: Diff, Pull request and Merge are keyed by the task's fix round, so
// a change the Coding agent reworked needs a new approval. Plan is asked once per task (round 0).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Governance;

/// <summary>Where an approval request stands.</summary>
public enum ApprovalStatus
{
    /// <summary>Waiting for an approver.</summary>
    Pending,

    /// <summary>Approved.</summary>
    Approved,

    /// <summary>Rejected.</summary>
    Rejected,
}

/// <summary>One approval request for a step of a task.</summary>
/// <param name="Id">The request.</param>
/// <param name="TenantId">Its tenant.</param>
/// <param name="BoardId">The project (board) of the task.</param>
/// <param name="SessionId">The task.</param>
/// <param name="IssueNumber">The task's ticket.</param>
/// <param name="Gate">The step it holds.</param>
/// <param name="Run">The task's fix round it was asked for (0 for <see cref="ApprovalGate.Plan"/>).</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Title">What it is about, in a line ("#12 Add dark mode").</param>
/// <param name="Link">What to look at: the pull request, or the branch comparison before one exists.</param>
/// <param name="RequestedBy">Who asked ("Autopilot", or a person).</param>
/// <param name="RequestedAtUtc">When.</param>
/// <param name="DecidedBy">Who decided, when decided.</param>
/// <param name="DecidedAtUtc">When decided.</param>
/// <param name="Note">The approver's note (a reason for a rejection).</param>
public sealed record ApprovalRequest(
    Guid Id,
    string TenantId,
    Guid BoardId,
    Guid SessionId,
    int IssueNumber,
    ApprovalGate Gate,
    int Run,
    ApprovalStatus Status,
    string Title,
    string? Link,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc,
    string? DecidedBy,
    DateTimeOffset? DecidedAtUtc,
    string? Note)
{
    /// <summary>The longest note kept.</summary>
    public const int MaxNote = 2000;

    /// <summary>A new pending request.</summary>
    public static ApprovalRequest New(string tenantId, Guid boardId, Guid sessionId, int issueNumber, ApprovalGate gate, int run,
        string title, string? link, string requestedBy, DateTimeOffset at) =>
        new(Guid.NewGuid(), tenantId, boardId, sessionId, issueNumber, gate, gate == ApprovalGate.Plan ? 0 : Math.Max(0, run),
            ApprovalStatus.Pending, title, link, requestedBy, at, null, null, null);

    /// <summary>The run key a gate is asked for: Plan once per task, every other gate per fix round.</summary>
    public static int RunFor(ApprovalGate gate, int fixRounds) => gate == ApprovalGate.Plan ? 0 : Math.Max(0, fixRounds);

    /// <summary>"Rejected by alice: not this way" — the sentence a rejection stops the task with.</summary>
    public string RejectionReason =>
        $"{char.ToUpperInvariant(ApprovalPolicy.Describe(Gate)[0])}{ApprovalPolicy.Describe(Gate)[1..]} was rejected by {DecidedBy ?? "an approver"}"
        + (string.IsNullOrWhiteSpace(Note) ? "." : $": {Note.Trim()}");
}

/// <summary>A person's decision on a request.</summary>
public sealed record ApprovalDecision(bool Approve, string DecidedBy, string? Note, DateTimeOffset AtUtc);

/// <summary>Tenant-scoped approval requests. Every call names the tenant (a background pass or a circuit has no ambient one).</summary>
public interface IApprovalStore
{
    /// <summary>The request for (task, gate, run), or null.</summary>
    Task<ApprovalRequest?> GetAsync(string tenantId, Guid sessionId, ApprovalGate gate, int run, CancellationToken ct = default);

    /// <summary>One request by id, or null.</summary>
    Task<ApprovalRequest?> FindAsync(string tenantId, Guid id, CancellationToken ct = default);

    /// <summary>Store <paramref name="request"/> unless one exists for the same (task, gate, run) — then that one is
    /// returned unchanged (idempotent: two passes never ask twice).</summary>
    Task<ApprovalRequest> RequestAsync(ApprovalRequest request, CancellationToken ct = default);

    /// <summary>Decide a PENDING request; a decided one is returned unchanged (a decision is never overwritten — two
    /// approvers clicking at once can't flip it). Null when it doesn't exist in this tenant.</summary>
    Task<ApprovalRequest?> DecideAsync(string tenantId, Guid id, ApprovalDecision decision, CancellationToken ct = default);

    /// <summary>The tenant's newest requests (pending first is the caller's choice), at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<ApprovalRequest>> ListRecentAsync(string tenantId, int max = 50, CancellationToken ct = default);
}

/// <summary>Who can approve in a tenant.</summary>
public interface IApproverDirectory
{
    /// <summary>How many enabled people of the tenant hold the approver or admin role; null when it can't tell (no
    /// identity admin API, or it failed). Zero means nobody can approve: gates fail closed with that reason.</summary>
    Task<int?> CountApproversAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>What a step does about its gate now.</summary>
public enum ApprovalVerdict
{
    /// <summary>Not required, or approved: go ahead.</summary>
    Proceed,

    /// <summary>Required and not asked yet: ask, then wait.</summary>
    Request,

    /// <summary>Asked; waiting for an approver.</summary>
    Wait,

    /// <summary>An approver rejected it: stop (a person decides what next).</summary>
    Rejected,

    /// <summary>Required, but nobody in the tenant can approve: stop — never go ahead without an approval.</summary>
    NoApprover,
}

/// <summary>The verdict and, when it stops, why.</summary>
public sealed record ApprovalCheck(ApprovalVerdict Verdict, string? Reason, ApprovalRequest? Request)
{
    /// <summary>Go ahead.</summary>
    public static ApprovalCheck Proceed { get; } = new(ApprovalVerdict.Proceed, null, null);

    /// <summary>True when the step may run.</summary>
    public bool MayProceed => Verdict == ApprovalVerdict.Proceed;

    /// <summary>True when the step must stop for a person (rejected, or nobody can approve).</summary>
    public bool Stops => Verdict is ApprovalVerdict.Rejected or ApprovalVerdict.NoApprover;
}

/// <summary>The rules (pure).</summary>
public static class ApprovalRules
{
    /// <summary>The role that may approve (besides admin).</summary>
    public const string ApproverRole = "approver";

    /// <summary>The reason a step stops with when nobody can approve it.</summary>
    public static string NobodyCanApprove(ApprovalGate gate) =>
        $"This project requires an approval for {ApprovalPolicy.Describe(gate)}, but nobody in this workspace holds the approver or admin role — "
        + "nothing goes ahead without one. Give someone the Approver role (Settings › Users), then try again.";

    /// <summary>
    /// What a step does about <paramref name="gate"/>: proceed when the policy doesn't require it or <paramref name="current"/>
    /// is approved; otherwise FAIL CLOSED — rejected stops, a known zero <paramref name="approvers"/> stops, asked waits,
    /// not asked yet asks. <paramref name="approvers"/> null = unknown (the step asks and waits: still never proceeds).
    /// </summary>
    public static ApprovalCheck Evaluate(ApprovalPolicy policy, ApprovalGate gate, ApprovalRequest? current, int? approvers)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Requires(gate)) { return ApprovalCheck.Proceed; }
        if (current is not null && current.Gate != gate)
        {
            throw new ArgumentException($"The request is for {current.Gate}, not {gate}.", nameof(current));
        }
        return current?.Status switch
        {
            ApprovalStatus.Approved => new(ApprovalVerdict.Proceed, null, current),
            ApprovalStatus.Rejected => new(ApprovalVerdict.Rejected, current.RejectionReason, current),
            _ when approvers is 0 => new(ApprovalVerdict.NoApprover, NobodyCanApprove(gate), current),
            ApprovalStatus.Pending => new(ApprovalVerdict.Wait, $"Waiting for an approver to approve {ApprovalPolicy.Describe(gate)}.", current),
            _ => new(ApprovalVerdict.Request, $"Needs an approval for {ApprovalPolicy.Describe(gate)}.", null),
        };
    }

    /// <summary>True when someone with <paramref name="roles"/> may approve or reject (the approver or admin role).</summary>
    public static bool CanDecide(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Any(r => string.Equals(r, ApproverRole, StringComparison.Ordinal) || string.Equals(r, "admin", StringComparison.Ordinal));
    }
}
