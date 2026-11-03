// Repo safety (E1.5, GAP-18): before AgentOS.NET merges a pull request it reads, through the GitHub API, what the base
// branch's protection requires (classic branch protection AND active rulesets), the reviews, the head commit's checks and
// statuses, the changed files and the CODEOWNERS file — and MergeProtection (pure) refuses a merge the protection would
// refuse. Every read that fails leaves the stricter reading: protection that can't be read counts as "protected, details
// unknown" (GitHub's own "blocked" verdict and the CODEOWNERS file still apply), a team whose membership can't be read
// doesn't count as approving.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Governance;
using Octokit;

namespace AgentOs.Modules.Integration.Governance;

/// <summary>Reads the facts a merge decision needs from GitHub.</summary>
internal static class GitHubMergeGuard
{
    /// <summary>Everything <see cref="MergeProtection"/> reads about pull request <paramref name="pr"/>.</summary>
    public static async Task<MergeFacts> ReadAsync(IGitHubClient client, string owner, string repo, PullRequest pr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pr);
        ct.ThrowIfCancellationRequested();
        var baseRef = pr.Base?.Ref ?? string.Empty;
        var defaultBranch = await DefaultBranchAsync(client, owner, repo).ConfigureAwait(false) ?? baseRef;
        var protection = await ProtectionAsync(client, owner, repo, baseRef).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var reviews = await ReviewsAsync(client, owner, repo, pr.Number).ConfigureAwait(false);
        var checks = await ChecksAsync(client, owner, repo, pr.Head?.Sha).ConfigureAwait(false);
        var files = (await client.PullRequest.Files(owner, repo, pr.Number).ConfigureAwait(false))
            .SelectMany(f => f.PreviousFileName is { Length: > 0 } previous ? new[] { f.FileName, previous } : new[] { f.FileName })
            .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToList();
        ct.ThrowIfCancellationRequested();
        var codeOwners = await CodeOwnersAsync(client, owner, repo, baseRef).ConfigureAwait(false);
        var approved = codeOwners is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : await ApprovedOwnersAsync(client, codeOwners, reviews, pr.User?.Login).ConfigureAwait(false);
        return new MergeFacts(pr.Head?.Ref ?? string.Empty, baseRef, defaultBranch, pr.Draft, pr.MergeableState?.StringValue,
            pr.User?.Login, reviews, checks, files, protection, codeOwners, approved);
    }

    private static async Task<string?> DefaultBranchAsync(IGitHubClient client, string owner, string repo)
    {
        try
        {
            return (await client.Repository.Get(owner, repo).ConfigureAwait(false))?.DefaultBranch;
        }
        catch (ApiException)
        {
            return null;
        }
    }

    // Classic protection (needs admin rights to read) merged with active rulesets (readable with read access). Unreadable
    // classic protection on a branch GitHub says is protected → protected, details unknown.
    internal static async Task<BranchProtectionFacts> ProtectionAsync(IGitHubClient client, string owner, string repo, string branch)
    {
        var approvals = 0;
        var codeOwners = false;
        var conversations = false;
        var checks = new List<string>();
        var isProtected = false;
        var known = true;
        try
        {
            var classic = await client.Repository.Branch.GetBranchProtection(owner, repo, branch).ConfigureAwait(false);
            if (classic is not null)
            {
                isProtected = true;
                approvals = classic.RequiredPullRequestReviews?.RequiredApprovingReviewCount ?? 0;
                codeOwners = classic.RequiredPullRequestReviews?.RequireCodeOwnerReviews ?? false;
                conversations = classic.RequiredConversationResolution?.Enabled ?? false;
                if (classic.RequiredStatusChecks?.Contexts is { } contexts) { checks.AddRange(contexts); }
            }
        }
        catch (NotFoundException)
        {
            // Not protected by classic rules (GitHub answers 404 "Branch not protected").
        }
        catch (ApiException)
        {
            // 403: the token can't read protection settings. Does GitHub say the branch is protected?
            known = false;
            isProtected = await IsProtectedAsync(client, owner, repo, branch).ConfigureAwait(false) ?? true;
        }

        foreach (var rule in await RulesAsync(client, owner, repo, branch).ConfigureAwait(false))
        {
            switch (rule.Type)
            {
                case "pull_request":
                    isProtected = true;
                    approvals = Math.Max(approvals, rule.Parameters?.RequiredApprovingReviewCount ?? 0);
                    codeOwners |= rule.Parameters?.RequireCodeOwnerReview ?? false;
                    conversations |= rule.Parameters?.RequiredReviewThreadResolution ?? false;
                    break;
                case "required_status_checks":
                    isProtected = true;
                    checks.AddRange((rule.Parameters?.RequiredStatusChecks ?? []).Select(c => c.Context).Where(c => !string.IsNullOrWhiteSpace(c))!);
                    break;
                case "update" or "deletion" or "non_fast_forward" or "required_signatures" or "required_linear_history":
                    isProtected = true;
                    break;
            }
        }
        return isProtected
            ? new BranchProtectionFacts(true, known, approvals, codeOwners, checks.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), conversations)
            : BranchProtectionFacts.Unprotected;
    }

    private static async Task<bool?> IsProtectedAsync(IGitHubClient client, string owner, string repo, string branch)
    {
        try
        {
            return (await client.Repository.Branch.Get(owner, repo, branch).ConfigureAwait(false))?.Protected;
        }
        catch (ApiException)
        {
            return null;
        }
    }

    // GET /repos/{owner}/{repo}/rules/branches/{branch} — the active ruleset rules for the branch (Octokit 14 has no
    // wrapper). Unreadable → none (classic protection and GitHub's "blocked" verdict still apply).
    private static async Task<IReadOnlyList<GitHubBranchRule>> RulesAsync(IGitHubClient client, string owner, string repo, string branch)
    {
        try
        {
            var uri = new Uri($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/rules/branches/{Uri.EscapeDataString(branch)}", UriKind.Relative);
            var response = await client.Connection.Get<List<GitHubBranchRule>>(uri, new Dictionary<string, string>()).ConfigureAwait(false);
            return response?.Body ?? [];
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or InvalidCastException or FormatException or ArgumentException)
        {
            return [];
        }
    }

    // Each reviewer's LATEST review that counts (comments don't replace an approval; a dismissal does).
    private static async Task<IReadOnlyList<ReviewFact>> ReviewsAsync(IGitHubClient client, string owner, string repo, int number)
    {
        var all = await client.PullRequest.Review.GetAll(owner, repo, number).ConfigureAwait(false);
        // Oldest first, so the last review of each reviewer that counts is their latest.
        return all.Where(r => r.User?.Login is { Length: > 0 } && StateOf(r.State.Value) is not null)
            .OrderBy(r => r.SubmittedAt)
            .GroupBy(r => r.User.Login, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ReviewFact(g.Key, StateOf(g.Last().State.Value).GetValueOrDefault()))
            .ToList();
    }

    // The review states that count; a comment (or a pending review) neither approves nor replaces an approval.
    private static ReviewState? StateOf(PullRequestReviewState state) => state switch
    {
        PullRequestReviewState.Approved => ReviewState.Approved,
        PullRequestReviewState.ChangesRequested => ReviewState.ChangesRequested,
        PullRequestReviewState.Dismissed => ReviewState.Other,
        _ => null,
    };

    // Check runs and commit statuses on the head commit, by name.
    private static async Task<IReadOnlyList<CheckFact>> ChecksAsync(IGitHubClient client, string owner, string repo, string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) { return []; }
        var facts = new List<CheckFact>();
        var runs = await client.Check.Run.GetAllForReference(owner, repo, sha).ConfigureAwait(false);
        facts.AddRange(runs.CheckRuns.Select(r => new CheckFact(r.Name,
            r.Status.Value == CheckStatus.Completed
            && r.Conclusion?.Value is CheckConclusion.Success or CheckConclusion.Neutral or CheckConclusion.Skipped)));
        try
        {
            var combined = await client.Repository.Status.GetCombined(owner, repo, sha).ConfigureAwait(false);
            facts.AddRange((combined?.Statuses ?? []).Select(s => new CheckFact(s.Context, s.State.Value == CommitState.Success)));
        }
        catch (ApiException)
        {
            // No statuses readable: required contexts that only report as statuses read "has not reported" (refused).
        }
        return facts;
    }

    // The first CODEOWNERS GitHub would use, read from the base branch; null when there is none.
    internal static async Task<CodeOwners?> CodeOwnersAsync(IGitHubClient client, string owner, string repo, string baseRef)
    {
        foreach (var path in CodeOwners.Locations)
        {
            try
            {
                var found = await client.Repository.Content.GetAllContentsByRef(owner, repo, path, baseRef).ConfigureAwait(false);
                if (found is { Count: > 0 } && found[0].Content is { } text) { return CodeOwners.Parse(text); }
            }
            catch (NotFoundException)
            {
                // Try the next location.
            }
        }
        return null;
    }

    // Owner tokens an approving review satisfies: "@login" directly; "@org/team" when an approving reviewer is an active
    // member of the team (a membership that can't be read doesn't count). Emails are never matched (GitHub resolves them
    // to accounts we can't see) — such a file needs another owner's approval.
    internal static async Task<IReadOnlySet<string>> ApprovedOwnersAsync(IGitHubClient client, CodeOwners owners, IReadOnlyList<ReviewFact> reviews, string? author)
    {
        var approvers = reviews.Where(r => r.State == ReviewState.Approved && !string.Equals(r.Login, author, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Login.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var satisfied = new HashSet<string>(StringComparer.Ordinal);
        if (approvers.Count == 0) { return satisfied; }
        foreach (var token in owners.AllOwners().Where(t => t.StartsWith('@')))
        {
            var name = token[1..];
            var slash = name.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                if (approvers.Contains(name)) { satisfied.Add(token); }
                continue;
            }
            if (await TeamApprovedAsync(client, name[..slash], name[(slash + 1)..], approvers).ConfigureAwait(false)) { satisfied.Add(token); }
        }
        return satisfied;
    }

    private static async Task<bool> TeamApprovedAsync(IGitHubClient client, string org, string slug, IReadOnlySet<string> approvers)
    {
        try
        {
            var team = await client.Organization.Team.GetByName(org, slug).ConfigureAwait(false);
            foreach (var login in approvers)
            {
                try
                {
                    var membership = await client.Organization.Team.GetMembershipDetails(team.Id, login).ConfigureAwait(false);
                    if (membership?.State.Value == MembershipState.Active) { return true; }
                }
                catch (NotFoundException)
                {
                    // Not a member.
                }
            }
        }
        catch (ApiException)
        {
            // The team can't be read (no read:org): it doesn't count as approving.
        }
        return false;
    }
}

/// <summary>A ruleset rule as <c>GET /rules/branches/{branch}</c> returns it (Octokit maps snake_case JSON).</summary>
public sealed class GitHubBranchRule
{
    public string Type { get; set; } = string.Empty;

    public GitHubBranchRuleParameters? Parameters { get; set; }
}

/// <summary>The parameters of a <c>pull_request</c> or <c>required_status_checks</c> rule.</summary>
public sealed class GitHubBranchRuleParameters
{
    public int? RequiredApprovingReviewCount { get; set; }

    public bool? RequireCodeOwnerReview { get; set; }

    public bool? RequiredReviewThreadResolution { get; set; }

    public List<GitHubRequiredStatusCheck>? RequiredStatusChecks { get; set; }
}

/// <summary>One required status check of a ruleset.</summary>
public sealed class GitHubRequiredStatusCheck
{
    public string? Context { get; set; }
}
