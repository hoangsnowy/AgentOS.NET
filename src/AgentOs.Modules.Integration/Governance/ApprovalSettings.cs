// Approval policies (E1.5, ADR-0011: UI settings per tenant — Settings › Autopilot › Approvals). Which steps a person
// with the approver or admin role must approve — the plan, opening the pull request, the change (diff), the merge —
// whether pull requests open as drafts, and per-project lines that replace the tenant policy for one project
// ("web-shop: plan, merge, draft"). Read per call, so a change applies to the next pass without a restart.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Governance;

namespace AgentOs.Modules.Integration.Governance;

/// <summary>Declares the <c>Approvals:*</c> settings and reads the policy of a project.</summary>
public sealed class ApprovalSettings : ISettingsContributor
{
    /// <summary>The settings category (they show on Settings › Autopilot, in their own panel).</summary>
    public const string Category = "Autopilot";

    /// <summary>A person approves the plan before the Coding agent starts on a ticket (Autopilot).</summary>
    public const string PlanKey = "Approvals:Plan";

    /// <summary>A person approves the pushed branch before its pull request is opened.</summary>
    public const string PullRequestKey = "Approvals:PullRequest";

    /// <summary>A person approves the change (its diff) before it goes on to review, testing and merging.</summary>
    public const string DiffKey = "Approvals:Diff";

    /// <summary>A person approves the merge.</summary>
    public const string MergeKey = "Approvals:Merge";

    /// <summary>Pull requests open as drafts.</summary>
    public const string DraftPullRequestsKey = "Approvals:DraftPullRequests";

    /// <summary>Per-project lines that replace the tenant policy for one project.</summary>
    public const string ProjectsKey = "Approvals:Projects";

    /// <summary>Every key, in the order the Settings page shows them.</summary>
    public static IReadOnlyList<string> Keys { get; } = [PlanKey, PullRequestKey, DiffKey, MergeKey, DraftPullRequestsKey, ProjectsKey];

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return Gate(PlanKey, "Approve the plan",
            "Autopilot creates the task for a ticket but the Coding agent starts only after an approver approves it on Board › Tasks.");
        yield return Gate(PullRequestKey, "Approve opening the pull request",
            "After the Coding agent pushed its branch, the pull request is opened only once an approver approved it (they see the branch comparison). Applies to every Board task.");
        yield return Gate(DiffKey, "Approve the change",
            "After the pull request is opened, nothing reviews, tests or merges it until an approver approved the change. A reworked change (a fix round) needs a new approval.");
        yield return Gate(MergeKey, "Approve the merge",
            "With Build and merge, Autopilot merges a change only after an approver approved the merge — on top of green checks, review and testing. A reworked change needs a new approval.");
        yield return new(DraftPullRequestsKey, SettingKind.Boolean, Category, "Open pull requests as drafts")
        {
            DefaultValue = "false",
            Help = "Pull requests are opened as drafts, so nobody merges them by accident. Autopilot marks a draft ready for review only right before a merge it may make.",
        };
        yield return new(ProjectsKey, SettingKind.Text, Category, "Per project")
        {
            Help = "One project per line, replacing the policy above for it: \"project name: plan, pr, diff, merge, draft\" — or \"project name: none\". The project may be named by its id too.",
            Validator = ApprovalPolicy.ValidateOverrides,
        };
    }

    private static SettingDescriptor Gate(string key, string label, string help) =>
        new(key, SettingKind.Boolean, Category, label) { DefaultValue = "false", Help = help };

    /// <summary>The tenant's policy (no project line applied), read now.</summary>
    public static async ValueTask<ApprovalPolicy> ReadTenantPolicyAsync(ISettings settings, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var gates = ApprovalGates.None;
        if (await settings.GetBoolAsync(PlanKey, tenantId, false, ct).ConfigureAwait(false)) { gates |= ApprovalGates.Plan; }
        if (await settings.GetBoolAsync(PullRequestKey, tenantId, false, ct).ConfigureAwait(false)) { gates |= ApprovalGates.PullRequest; }
        if (await settings.GetBoolAsync(DiffKey, tenantId, false, ct).ConfigureAwait(false)) { gates |= ApprovalGates.Diff; }
        if (await settings.GetBoolAsync(MergeKey, tenantId, false, ct).ConfigureAwait(false)) { gates |= ApprovalGates.Merge; }
        var draft = await settings.GetBoolAsync(DraftPullRequestsKey, tenantId, false, ct).ConfigureAwait(false);
        return new ApprovalPolicy(gates, draft);
    }

    /// <summary>The policy of project <paramref name="projectName"/> (<paramref name="projectId"/>), read now: its
    /// per-project line when there is one, else the tenant's policy.</summary>
    public static async ValueTask<ApprovalPolicy> ReadPolicyAsync(ISettings settings, string tenantId, string? projectName, Guid projectId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var tenant = await ReadTenantPolicyAsync(settings, tenantId, ct).ConfigureAwait(false);
        var lines = await settings.GetStringAsync(ProjectsKey, tenantId, null, ct).ConfigureAwait(false);
        return ApprovalPolicy.Resolve(tenant, lines, projectName, projectId);
    }
}
