// M5 / multi-repo — contract for the issue-work agent: given a ticket + the repos it targets, run the
// agentic LLM loop (using runner_shell) per repo to implement a fix and push a branch in each, then
// return a per-repo outcome the caller turns into one PR per repo (cross-linked).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Sessions;

/// <summary>One repository a session targets.</summary>
/// <param name="SessionRepoId">The session-repo row this maps to (for per-repo result write-back).</param>
/// <param name="Owner">GitHub owner / org.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="DefaultBranch">Base branch the fix branch is cut from.</param>
/// <param name="ExistingBranch">Set for "Ask for changes": the branch of this repo's open pull request. The agent
/// checks it out and pushes more commits to it instead of starting a new branch.</param>
public sealed record WorkRepo(Guid SessionRepoId, string Owner, string Repo, string DefaultBranch, string? ExistingBranch = null)
{
    /// <summary>The branch a fresh attempt creates, chosen by the server so it never collides with one an earlier,
    /// interrupted attempt left on the remote (that push would need a force-push, which the agent may not do).
    /// Null = the default <c>issue-N-ai-fix</c>.</summary>
    public string? NewBranch { get; init; }

    /// <summary>The branch a ticket's first attempt uses.</summary>
    public static string DefaultBranchName(int issueNumber) =>
        "issue-" + issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-ai-fix";
}

/// <summary>Request to the issue-work agent — a ticket plus the repos it should be implemented in.</summary>
/// <param name="SessionId">Correlating session row for evidence / logging.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="MemberId">User who owns the session (used for runner targeting).</param>
/// <param name="Repos">The repos to implement the fix in (one branch + PR each).</param>
/// <param name="IssueNumber">Ticket number.</param>
/// <param name="IssueTitle">Ticket title (short context for the LLM).</param>
/// <param name="IssueBody">Ticket body / description (full context for the LLM).</param>
/// <param name="TicketKind">Issue / PullRequest / DraftIssue (informational).</param>
/// <param name="ProviderOverride">
/// Optional LLM provider name that overrides the configured <c>Agents:IssueWork:Provider</c> for this
/// run. Set to <c>"RemoteAgent"</c> to route the whole agentic loop to the member's paired dev-machine
/// CLI (claude-code / codex) — it runs on their own machine + subscription, spending zero server tokens.
/// Null = use the configured provider (server-side LLM + <c>runner_shell</c>).
/// </param>
/// <param name="CliProfile">
/// Optional CLI-agent profile for the <c>RemoteAgent</c> path (e.g. <c>"claude"</c>, <c>"codex"</c>) —
/// which subscription CLI the paired runner should invoke. Null = the runner's configured default.
/// </param>
public sealed record IssueWorkRequest(
    Guid SessionId,
    string TenantId,
    string MemberId,
    IReadOnlyList<WorkRepo> Repos,
    int IssueNumber,
    string IssueTitle,
    string IssueBody,
    string TicketKind = "Issue",
    string? ProviderOverride = null,
    string? CliProfile = null,
    string? TicketType = null,
    string? ChangeRequest = null);

/// <summary>The agent's outcome for one repo.</summary>
/// <param name="SessionRepoId">The session-repo row this outcome belongs to.</param>
/// <param name="Ok">True when a branch was pushed for this repo.</param>
/// <param name="Owner">Repo owner.</param>
/// <param name="Repo">Repo name.</param>
/// <param name="BranchName">Pushed branch (e.g. <c>issue-42-ai-fix</c>). Empty on failure.</param>
/// <param name="Summary">One-sentence description of the change.</param>
/// <param name="Error">Failure reason; null when <see cref="Ok"/>.</param>
public sealed record RepoWorkOutcome(
    Guid SessionRepoId, bool Ok, string Owner, string Repo, string BranchName, string Summary, string? Error)
{
    /// <summary>The pull request description the agent wrote (Markdown: what changed and why, how it was verified, and
    /// anything the ticket asks the pull request itself to contain). On a follow-up it is the complete updated
    /// description and replaces the old one. Null when the agent gave none — the one-sentence summary is used.</summary>
    public string? Description { get; init; }
}

/// <summary>Result from the issue-work agent across all target repos.</summary>
/// <param name="Ok">True when every target repo pushed a branch.</param>
/// <param name="Repos">Per-repo outcomes.</param>
/// <param name="Error">Aggregate failure reason; null when <see cref="Ok"/>.</param>
public sealed record IssueWorkResult(bool Ok, IReadOnlyList<RepoWorkOutcome> Repos, string? Error);

/// <summary>Runs the agentic LLM loop for a ticket against the paired dev-machine runner, per target repo.</summary>
public interface IIssueWorkAgent
{
    Task<IssueWorkResult> RunAsync(IssueWorkRequest request, CancellationToken ct = default);
}
