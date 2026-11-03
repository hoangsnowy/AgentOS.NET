// AgentOs.Domain/Governance/ApprovalPolicy.cs
// Approval policies (E1.5): which steps of a ticket a person must approve before AgentOS.NET takes them, per tenant and
// per project (workspace). Four gates, in the order a ticket meets them:
//   Plan         — the Coding agent starts on a ticket (Autopilot only: a person who clicks Run already decided).
//   Pull request — the pushed branch becomes a pull request (every Board task: no person has seen the code yet).
//   Diff         — the opened change goes on to review, testing and merging (a person reads the diff first).
//   Merge        — the change is merged (Autopilot's "Build and merge").
// Plus "draft": pull requests are opened as drafts (marked ready only right before an approved merge).
// The tenant policy is Settings › Autopilot › Approvals; a project line ("web-shop: plan, merge, draft") replaces it for
// that project. Pure: parsing, resolution and evaluation are unit-tested without I/O.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AgentOs.Domain.Governance;

/// <summary>A step a person may have to approve.</summary>
public enum ApprovalGate
{
    /// <summary>Starting the Coding agent on a ticket (Autopilot).</summary>
    Plan,

    /// <summary>The change going on to review, testing and merging after it was opened.</summary>
    Diff,

    /// <summary>Opening the pull request from the pushed branch.</summary>
    PullRequest,

    /// <summary>Merging the change.</summary>
    Merge,
}

/// <summary>The gates a policy requires (a set of <see cref="ApprovalGate"/>).</summary>
[Flags]
public enum ApprovalGates
{
    /// <summary>Nothing needs a person.</summary>
    None = 0,

    /// <summary><see cref="ApprovalGate.Plan"/>.</summary>
    Plan = 1,

    /// <summary><see cref="ApprovalGate.Diff"/>.</summary>
    Diff = 2,

    /// <summary><see cref="ApprovalGate.PullRequest"/>.</summary>
    PullRequest = 4,

    /// <summary><see cref="ApprovalGate.Merge"/>.</summary>
    Merge = 8,
}

/// <summary>What a project requires: the gates a person approves, and whether pull requests open as drafts.</summary>
/// <param name="Required">The gates that need an approval.</param>
/// <param name="DraftPullRequests">Pull requests are opened as drafts.</param>
public sealed record ApprovalPolicy(ApprovalGates Required, bool DraftPullRequests)
{
    /// <summary>No approvals, ready pull requests — the built-in policy.</summary>
    public static ApprovalPolicy None { get; } = new(ApprovalGates.None, false);

    /// <summary>True when <paramref name="gate"/> needs an approval.</summary>
    public bool Requires(ApprovalGate gate) => (Required & FlagOf(gate)) != 0;

    /// <summary>The flag of one gate.</summary>
    public static ApprovalGates FlagOf(ApprovalGate gate) => gate switch
    {
        ApprovalGate.Plan => ApprovalGates.Plan,
        ApprovalGate.Diff => ApprovalGates.Diff,
        ApprovalGate.PullRequest => ApprovalGates.PullRequest,
        ApprovalGate.Merge => ApprovalGates.Merge,
        _ => throw new ArgumentOutOfRangeException(nameof(gate), gate, null),
    };

    /// <summary>The gates in the order a ticket meets them.</summary>
    public static IReadOnlyList<ApprovalGate> InOrder { get; } = [ApprovalGate.Plan, ApprovalGate.PullRequest, ApprovalGate.Diff, ApprovalGate.Merge];

    /// <summary>A gate in plain words ("the plan", "opening the pull request", …).</summary>
    public static string Describe(ApprovalGate gate) => gate switch
    {
        ApprovalGate.Plan => "the plan",
        ApprovalGate.Diff => "the change",
        ApprovalGate.PullRequest => "opening the pull request",
        ApprovalGate.Merge => "the merge",
        _ => gate.ToString(),
    };

    /// <summary>A short label for a gate ("Plan", "Diff", "Pull request", "Merge").</summary>
    public static string Label(ApprovalGate gate) => gate == ApprovalGate.PullRequest ? "Pull request" : gate.ToString();

    /// <summary>The policy in a few words, for the Autopilot log and the Settings help ("plan, merge · draft PRs").</summary>
    public string Summary
    {
        get
        {
            var gates = InOrder.Where(Requires).Select(g => Label(g).ToLowerInvariant()).ToList();
            var text = gates.Count == 0 ? "no approvals" : "approve " + string.Join(", ", gates);
            return DraftPullRequests ? text + " · draft pull requests" : text;
        }
    }

    /// <summary>
    /// The policy for one project: the line of <paramref name="projectOverrides"/> that names it (by name, any case, or
    /// by id) replaces <paramref name="tenantPolicy"/>; no line → the tenant policy. A line that doesn't parse is
    /// ignored here (the setting's validator refuses it on save).
    /// </summary>
    public static ApprovalPolicy Resolve(ApprovalPolicy tenantPolicy, string? projectOverrides, string? projectName, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(tenantPolicy);
        foreach (var (project, policy, _) in ParseOverrides(projectOverrides))
        {
            if (policy is null) { continue; }
            if (string.Equals(project, projectName?.Trim(), StringComparison.OrdinalIgnoreCase)
                || (Guid.TryParse(project, out var id) && id == projectId && projectId != Guid.Empty))
            {
                return policy;
            }
        }
        return tenantPolicy;
    }

    /// <summary>Validate the per-project lines; null when valid, else the first problem in plain words.</summary>
    public static string? ValidateOverrides(string? text)
    {
        if (text is { Length: > 4000 }) { return "Keep the list under 4000 characters."; }
        foreach (var (project, _, error) in ParseOverrides(text))
        {
            if (error is not null) { return string.IsNullOrEmpty(project) ? error : $"{project}: {error}"; }
        }
        return null;
    }

    /// <summary>Parse a gate list ("plan, pr, merge, draft" — or "none"); the error names the word it didn't know.</summary>
    public static (ApprovalPolicy? Policy, string? Error) ParseGates(string? list)
    {
        var gates = ApprovalGates.None;
        var draft = false;
        var none = false;
        foreach (var raw in (list ?? string.Empty).Split([',', ';', ' ', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "plan": gates |= ApprovalGates.Plan; break;
                case "diff": gates |= ApprovalGates.Diff; break;
                case "pr" or "pull-request" or "pullrequest": gates |= ApprovalGates.PullRequest; break;
                case "merge": gates |= ApprovalGates.Merge; break;
                case "draft" or "drafts": draft = true; break;
                case "none": none = true; break;
                default:
                    return (null, string.Create(CultureInfo.InvariantCulture,
                        $"\"{raw}\" is not a gate — use plan, diff, pr, merge, draft or none."));
            }
        }
        if (none && (gates != ApprovalGates.None || draft)) { return (null, "\"none\" can't be combined with other gates."); }
        return (new ApprovalPolicy(gates, draft), null);
    }

    // One "project: gates" per line; '#' starts a comment. The project is a name or an id.
    private static IEnumerable<(string Project, ApprovalPolicy? Policy, string? Error)> ParseOverrides(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { yield break; }
        foreach (var rawLine in text.Split('\n'))
        {
            var hash = rawLine.IndexOf('#', StringComparison.Ordinal);
            var line = (hash >= 0 ? rawLine[..hash] : rawLine).Trim();
            if (line.Length == 0) { continue; }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                yield return (string.Empty, null, $"\"{line}\" needs the form \"project: plan, merge, draft\".");
                continue;
            }
            var project = line[..colon].Trim();
            var (policy, error) = ParseGates(line[(colon + 1)..]);
            yield return (project, policy, error);
        }
    }
}
