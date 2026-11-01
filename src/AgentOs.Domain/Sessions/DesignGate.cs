// AgentOs.Domain/Sessions/DesignGate.cs
// Design gate (ADR-0016): a UI ticket can't merge looking "vibe-coded". After the Testing agent passes a change it also
// takes Playwright screenshots of the screens it touches (390 / 1280 px, light / dark); the runner sends them back to
// the tenant's file store under a design scope (never into git). The Designer agent then looks at them — with the
// repository's own design skill and the ticket — and says pass, changes (findings: screen, problem, fix) or
// needs-person. The owner can override it on Board › Tasks (Approve design / Ask for changes). Autopilot merges a
// ticket whose flow runs Design only after a pass or an owner approval; "changes" go back to Coding as a fix round.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Domain.Sessions;

/// <summary>Scope keys of design screenshots in the tenant's file store (<see cref="IScopedFileStore"/>).</summary>
public static class DesignScope
{
    /// <summary>The screenshots of ticket #<paramref name="issueNumber"/> on board <paramref name="boardId"/>, taken by
    /// test run <paramref name="run"/> (the task's fix round when it was tested: 0 = the first attempt).</summary>
    public static string For(Guid boardId, int issueNumber, int run) =>
        string.Create(CultureInfo.InvariantCulture, $"design:{boardId:N}:{issueNumber}:{run}");
}

/// <summary>One screenshot the Testing agent took.</summary>
/// <param name="Path">Its path in the design scope (relative to the run folder it came from).</param>
/// <param name="Screen">The screen's name ("Checkout").</param>
/// <param name="Width">The viewport width in CSS pixels (390 = phone, 1280 = desktop).</param>
/// <param name="Theme"><c>light</c> or <c>dark</c>.</param>
public sealed partial record DesignShot(string Path, string Screen, int Width, string Theme)
{
    /// <summary>The phone width the Testing agent is asked for.</summary>
    public const int PhoneWidth = 390;

    /// <summary>The desktop width the Testing agent is asked for.</summary>
    public const int DesktopWidth = 1280;

    /// <summary>A phone-sized screenshot (up to 600 px wide).</summary>
    public bool IsPhone => Width is > 0 and <= 600;

    /// <summary>A dark-scheme screenshot.</summary>
    public bool IsDark => string.Equals(Theme, "dark", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Checkout · phone · dark".</summary>
    public string Caption => $"{Screen} · {(IsPhone ? "phone" : "desktop")} · {(IsDark ? "dark" : "light")}";

    /// <summary>What a stored path says when the agent's reply didn't: <c>&lt;screen&gt;-&lt;width&gt;-&lt;theme&gt;.png</c>
    /// (the name the agent is asked to use); unknown parts default to the file name, desktop and light.</summary>
    public static DesignShot FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var m = NamePattern().Match(stem);
        if (!m.Success) { return new(path, stem, DesktopWidth, "light"); }
        var width = int.TryParse(m.Groups["w"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var w) ? w : DesktopWidth;
        var screen = m.Groups["screen"].Value.Replace('-', ' ').Replace('_', ' ').Trim();
        return new(path, screen.Length == 0 ? stem : char.ToUpperInvariant(screen[0]) + screen[1..], width,
            string.Equals(m.Groups["theme"].Value, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light");
    }

    [GeneratedRegex(@"^(?<screen>.+?)[-_](?<w>\d{3,4})(px)?[-_](?<theme>light|dark)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>What the Designer agent decided.</summary>
public enum DesignOutcome
{
    /// <summary>The screens look right: on to Merge.</summary>
    Pass,

    /// <summary>Concrete problems to fix: back to Coding with the findings.</summary>
    Changes,

    /// <summary>The agent can't judge (no screenshots, a taste call, a broken capture): a person decides.</summary>
    NeedsPerson,
}

/// <summary>One design finding: which screen, what is wrong, and how to fix it.</summary>
public sealed record DesignFinding(string Screen, string Problem, string Fix);

/// <summary>The Designer agent's verdict. <see cref="Ran"/> is false when it couldn't look at all (no computer, …).</summary>
public sealed record DesignVerdict(bool Ran, DesignOutcome Outcome, IReadOnlyList<DesignFinding> Findings, string Summary, string? Error)
{
    /// <summary>A verdict for a run that couldn't happen.</summary>
    public static DesignVerdict NotRun(string why) => new(false, DesignOutcome.NeedsPerson, [], why, why);

    /// <summary>The findings as the change request sent back to the Coding agent.</summary>
    public string ChangeRequest()
    {
        var lines = Findings.Select(f => $"- {(string.IsNullOrWhiteSpace(f.Screen) ? "(every screen)" : f.Screen)}: {f.Problem}"
            + (string.IsNullOrWhiteSpace(f.Fix) ? "" : $" — fix: {f.Fix}")).Prepend(Summary);
        return "Design review asked for changes. Fix every finding so the screens match the project's design (check them at 390 and 1280 px, light and dark):\n"
            + string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }
}

/// <summary>A screenshot with its bytes, as the Designer agent gets it.</summary>
public sealed record DesignScreenshot(DesignShot Shot, byte[] Content, string ContentType);

/// <summary>What the Designer agent checks: the ticket, its screenshots, and the repository's design skill.</summary>
public sealed record DesignRequest(
    Guid SessionId,
    string TenantId,
    string MemberId,
    WorkRepo Repo,
    int IssueNumber,
    string IssueTitle,
    string IssueBody,
    IReadOnlyList<DesignScreenshot> Screenshots,
    string? CliProfile = null)
{
    /// <summary>The repository's design skill(s) — <c>.claude/skills/*design*/SKILL.md</c> on its default branch, budgeted.</summary>
    public IReadOnlyList<RepoInstructionFile> DesignSkills { get; init; } = [];
}

/// <summary>Checks a UI change against its screenshots. Read-only: it never edits, commits or pushes.</summary>
public interface IDesignAgent
{
    /// <summary>Look at <c>request.Screenshots</c> against the ticket and the design skill.</summary>
    Task<DesignVerdict> RunAsync(DesignRequest request, CancellationToken ct = default);
}

/// <summary>Asks the Testing agent for screenshots of the screens its change touches, sent back to <see cref="ScopeKey"/>.</summary>
public sealed record DesignCapture(string ScopeKey)
{
    /// <summary>The caps on what the run may send back.</summary>
    public RunCollect Limits { get; init; } = RunCollect.Screenshots;

    /// <summary>The folder this run's screenshots are stored under inside the scope ("" = the scope's root): one per repo
    /// of a multi-repo task (<see cref="PrefixFor"/>), so repo B's <c>home-390-light.png</c> never replaces repo A's.</summary>
    public string PathPrefix { get; init; } = string.Empty;

    /// <summary>The folder of one repo of the task (its task-repo id).</summary>
    public static string PrefixFor(Guid sessionRepoId) => string.Create(CultureInfo.InvariantCulture, $"{sessionRepoId:N}/");
}

/// <summary>Where a design review stands.</summary>
public enum DesignReviewStatus
{
    /// <summary>Screenshots are in; the Designer agent hasn't looked yet.</summary>
    Pending,

    /// <summary>The Designer agent is looking now.</summary>
    Checking,

    /// <summary>The Designer agent passed it.</summary>
    Pass,

    /// <summary>The Designer agent asked for changes.</summary>
    Changes,

    /// <summary>The Designer agent left it to a person.</summary>
    NeedsPerson,

    /// <summary>The Designer agent couldn't run (no computer, budget, …).</summary>
    NotRun,
}

/// <summary>The owner's decision, which overrides the agent.</summary>
public enum DesignDecision
{
    /// <summary>Approve design: Autopilot may merge.</summary>
    Approved,

    /// <summary>Ask for changes: back to Coding with the owner's note.</summary>
    ChangesAsked,
}

/// <summary>Where the design gate of a task stands, as Autopilot reads it.</summary>
public enum DesignGateState
{
    /// <summary>Nothing checked yet for the current change.</summary>
    NotChecked,

    /// <summary>The Designer agent is looking.</summary>
    Checking,

    /// <summary>The Designer agent passed it, or the owner approved it.</summary>
    Passed,

    /// <summary>Changes requested — by the agent, or by the owner.</summary>
    ChangesRequested,

    /// <summary>A person must decide (Approve design / Ask for changes).</summary>
    NeedsPerson,
}

/// <summary>One design review: a task's test run, its screenshots, the agent's verdict and the owner's decision.</summary>
public sealed record DesignReview(
    Guid Id,
    string TenantId,
    Guid BoardId,
    Guid SessionId,
    int IssueNumber,
    int Run,
    DesignReviewStatus Status,
    string Summary,
    IReadOnlyList<DesignFinding> Findings,
    IReadOnlyList<DesignShot> Shots,
    string? Error,
    DesignDecision? Decision,
    string? DecisionNote,
    string? DecidedBy,
    DateTimeOffset? DecidedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>How many automatic re-runs of a check that couldn't run (<see cref="DesignReviewStatus.NotRun"/>: computer
    /// offline, budget, timeout) Autopilot has made for this run — at most <see cref="MaxAutomaticRetries"/>.</summary>
    public int Retries { get; init; }

    /// <summary>The most automatic re-runs of a check that couldn't run, per task and test run.</summary>
    public const int MaxAutomaticRetries = 2;

    /// <summary>The check couldn't run, nobody decided yet, and Autopilot may run it again (once a computer is there).</summary>
    public bool CanRetryAutomatically => Status == DesignReviewStatus.NotRun && Decision is null && Retries < MaxAutomaticRetries;

    /// <summary>Where the screenshots of this review live.</summary>
    public string ScopeKey => DesignScope.For(BoardId, IssueNumber, Run);

    /// <summary>A fresh review of run <paramref name="run"/>.</summary>
    public static DesignReview New(string tenantId, Guid boardId, Guid sessionId, int issueNumber, int run, DesignReviewStatus status) =>
        new(Guid.NewGuid(), tenantId, boardId, sessionId, issueNumber, run, status, string.Empty, [], [], null, null, null, null, null, DateTimeOffset.UtcNow);

    /// <summary>The gate: the owner's decision wins over the agent's verdict.</summary>
    public DesignGateState Gate => Decision switch
    {
        DesignDecision.Approved => DesignGateState.Passed,
        DesignDecision.ChangesAsked => DesignGateState.ChangesRequested,
        _ => Status switch
        {
            DesignReviewStatus.Pending => DesignGateState.NotChecked,
            DesignReviewStatus.Checking => DesignGateState.Checking,
            DesignReviewStatus.Pass => DesignGateState.Passed,
            DesignReviewStatus.Changes => DesignGateState.ChangesRequested,
            _ => DesignGateState.NeedsPerson,
        },
    };

    /// <summary>What goes back to the Coding agent when the gate asks for changes.</summary>
    public string ChangeRequest() => Decision == DesignDecision.ChangesAsked
        ? "The project owner looked at the screenshots and asked for design changes:\n" + (DecisionNote ?? string.Empty).Trim()
          + (Findings.Count > 0 ? "\n\nThe Designer agent's findings, for reference:\n" + string.Join("\n", Findings.Select(f => $"- {f.Screen}: {f.Problem} — fix: {f.Fix}")) : "")
        : new DesignVerdict(true, DesignOutcome.Changes, Findings, Summary, null).ChangeRequest();
}

/// <summary>A change to the agent's side of a review — never the owner's decision. Null fields are kept as they are.</summary>
/// <param name="Status">The new status.</param>
/// <param name="Summary">The verdict's summary, or null to keep it.</param>
/// <param name="Findings">The findings, or null to keep them.</param>
/// <param name="Shots">The screenshots, or null to keep them.</param>
/// <param name="Error">Why the check couldn't run; null clears it.</param>
/// <param name="CountRetry">This run is an automatic re-run of a check that couldn't run: count it.</param>
public sealed record DesignVerdictUpdate(DesignReviewStatus Status, string? Summary = null, IReadOnlyList<DesignFinding>? Findings = null,
    IReadOnlyList<DesignShot>? Shots = null, string? Error = null, bool CountRetry = false);

/// <summary>The owner's decision on a review.</summary>
public sealed record DesignOwnerDecision(DesignDecision Decision, string? Note, string DecidedBy, DateTimeOffset DecidedAtUtc);

/// <summary>
/// Tenant-scoped store of design reviews (one per task and test run). Implemented by the Sessions module.
/// There is no whole-row save: the agent's side (<see cref="SaveVerdictAsync"/>) and the owner's decision
/// (<see cref="DecideAsync"/>) are written by narrow updates that touch only their own columns, so a verdict that lands
/// after the owner decided (a check runs for up to half an hour) can never erase the decision — and a decision made
/// from a stale page can never erase a verdict.
/// </summary>
public interface IDesignReviewStore
{
    /// <summary>The review of run <paramref name="run"/> of a task, or null.</summary>
    Task<DesignReview?> GetAsync(string tenantId, Guid sessionId, int run, CancellationToken ct = default);

    /// <summary>A task's latest review (highest run), or null.</summary>
    Task<DesignReview?> LatestAsync(string tenantId, Guid sessionId, CancellationToken ct = default);

    /// <summary>A task's reviews, newest run first.</summary>
    Task<IReadOnlyList<DesignReview>> ListForTaskAsync(string tenantId, Guid sessionId, CancellationToken ct = default);

    /// <summary>The tenant's most recent reviews, newest first (Board › Tasks).</summary>
    Task<IReadOnlyList<DesignReview>> ListRecentAsync(string tenantId, int max = 50, CancellationToken ct = default);

    /// <summary>The task's review of <c>fresh.Run</c>: stored as <paramref name="fresh"/> when there is none yet, else the
    /// existing one, untouched.</summary>
    Task<DesignReview> EnsureAsync(DesignReview fresh, CancellationToken ct = default);

    /// <summary>Write the agent's side of a review (status, summary, findings, screenshots, error, retries) — never the
    /// owner's decision. Null when there is no such review.</summary>
    Task<DesignReview?> SaveVerdictAsync(string tenantId, Guid sessionId, int run, DesignVerdictUpdate update, CancellationToken ct = default);

    /// <summary>Write the owner's decision — nothing else. Null when there is no such review.</summary>
    Task<DesignReview?> DecideAsync(string tenantId, Guid sessionId, int run, DesignOwnerDecision decision, CancellationToken ct = default);
}
