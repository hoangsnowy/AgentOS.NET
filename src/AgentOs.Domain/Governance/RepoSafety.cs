// AgentOs.Domain/Governance/RepoSafety.cs
// Repo safety (E1.5, threat model GAP-18): AgentOS.NET never writes to a repository's default branch and never merges a
// pull request its branch protection would refuse. The server reads the protection, the reviews, the checks and the
// CODEOWNERS file through the provider's API (GitHubMergeGuard) and asks these rules — pure, so every refusal is
// unit-tested. GitHub's own "blocked" verdict is honoured too: a token whose owner may bypass protection (an admin, when
// "include administrators" is off) must not let an agent do what the rules would stop a person from doing.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOs.Domain.Governance;

/// <summary>Branch rules every write path checks.</summary>
public static class RepoSafety
{
    /// <summary>True when <paramref name="branch"/> is one of <paramref name="defaultBranches"/> (the workspace's base and
    /// the repository's default). <c>refs/heads/</c> and surrounding blanks are ignored; an empty branch counts as the
    /// default (a push with no branch name lands on whatever HEAD is — never allowed).</summary>
    public static bool IsDefaultBranch(string? branch, params string?[] defaultBranches)
    {
        var name = Normalize(branch);
        if (name.Length == 0) { return true; }
        return defaultBranches.Any(d => Normalize(d) is { Length: > 0 } b && string.Equals(b, name, StringComparison.Ordinal));
    }

    /// <summary>The reason a change from <paramref name="branch"/> is refused, or null when it may go ahead.</summary>
    public static string? RefuseDefaultBranch(string? branch, params string?[] defaultBranches) =>
        IsDefaultBranch(branch, defaultBranches)
            ? $"Refusing to use the default branch '{Normalize(branch)}' as the change's branch — AgentOS.NET only pushes to and merges from its own branches."
            : null;

    private static string Normalize(string? branch)
    {
        var b = (branch ?? string.Empty).Trim();
        return b.StartsWith("refs/heads/", StringComparison.Ordinal) ? b["refs/heads/".Length..] : b;
    }
}

/// <summary>What the base branch's protection (classic rules and rulesets, merged) requires.</summary>
/// <param name="Protected">The branch is protected (classic protection or an active ruleset).</param>
/// <param name="DetailsKnown">The required reviews/checks could be read (a token without admin rights can see that a
/// branch is protected but not always how).</param>
/// <param name="RequiredApprovals">Approving reviews required.</param>
/// <param name="RequireCodeOwnerReviews">A code owner of every changed file must approve.</param>
/// <param name="RequiredChecks">Status checks that must pass (by context / check name).</param>
/// <param name="RequireConversationResolution">Every review conversation must be resolved.</param>
public sealed record BranchProtectionFacts(
    bool Protected,
    bool DetailsKnown,
    int RequiredApprovals,
    bool RequireCodeOwnerReviews,
    IReadOnlyList<string> RequiredChecks,
    bool RequireConversationResolution)
{
    /// <summary>An unprotected branch.</summary>
    public static BranchProtectionFacts Unprotected { get; } = new(false, true, 0, false, [], false);
}

/// <summary>A reviewer's latest review state.</summary>
public enum ReviewState
{
    /// <summary>Approved.</summary>
    Approved,

    /// <summary>Requested changes.</summary>
    ChangesRequested,

    /// <summary>Commented, dismissed or pending — neither.</summary>
    Other,
}

/// <summary>One reviewer's LATEST review.</summary>
public sealed record ReviewFact(string Login, ReviewState State);

/// <summary>One check on the head commit.</summary>
/// <param name="Name">Check-run name or status context.</param>
/// <param name="Passed">Completed successfully (success, neutral or skipped).</param>
public sealed record CheckFact(string Name, bool Passed);

/// <summary>Everything the merge rules read about one pull request.</summary>
/// <param name="HeadBranch">The branch it merges from.</param>
/// <param name="BaseBranch">The branch it merges into.</param>
/// <param name="DefaultBranch">The repository's default branch.</param>
/// <param name="Draft">Still a draft.</param>
/// <param name="MergeableState">GitHub's <c>mergeable_state</c> (clean, blocked, dirty, behind, unstable, unknown, …).</param>
/// <param name="Author">Who opened it (their own approval never counts).</param>
/// <param name="Reviews">Each reviewer's latest review.</param>
/// <param name="Checks">Checks and statuses on the head commit.</param>
/// <param name="ChangedFiles">The paths it changes.</param>
/// <param name="Protection">The base branch's protection.</param>
/// <param name="CodeOwners">The base branch's CODEOWNERS (null when there is none).</param>
/// <param name="ApprovedOwners">Owner tokens (<c>@user</c>, <c>@org/team</c>, lower case) satisfied by an approving
/// review — a team counts when one of its members approved (resolved by the reader).</param>
public sealed record MergeFacts(
    string HeadBranch,
    string BaseBranch,
    string DefaultBranch,
    bool Draft,
    string? MergeableState,
    string? Author,
    IReadOnlyList<ReviewFact> Reviews,
    IReadOnlyList<CheckFact> Checks,
    IReadOnlyList<string> ChangedFiles,
    BranchProtectionFacts Protection,
    CodeOwners? CodeOwners,
    IReadOnlySet<string> ApprovedOwners);

/// <summary>The merge rules (pure): every reason the merge is refused, empty when it may go ahead.</summary>
public static class MergeProtection
{
    /// <summary>All reasons a merge of <paramref name="facts"/> is refused (empty = allowed).</summary>
    public static IReadOnlyList<string> Evaluate(MergeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var reasons = new List<string>();
        if (RepoSafety.IsDefaultBranch(facts.HeadBranch, facts.DefaultBranch, facts.BaseBranch))
        {
            reasons.Add($"its branch '{facts.HeadBranch}' is the default branch — AgentOS.NET never merges from it");
        }
        if (facts.Draft) { reasons.Add("it is still a draft"); }
        switch (facts.MergeableState?.Trim().ToLowerInvariant())
        {
            case "blocked":
                reasons.Add("GitHub reports the merge is blocked by the branch's protection rules (reviews, checks or conversations)");
                break;
            case "dirty":
                reasons.Add("it conflicts with its base branch");
                break;
        }

        var p = facts.Protection;
        var approvers = facts.Reviews
            .Where(r => r.State == ReviewState.Approved && !string.Equals(r.Login, facts.Author, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Login).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (p.RequiredApprovals > 0 && approvers < p.RequiredApprovals)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture,
                $"the branch needs {p.RequiredApprovals} approving review(s), it has {approvers}"));
        }
        if ((p.RequiredApprovals > 0 || p.RequireCodeOwnerReviews) && facts.Reviews.Any(r => r.State == ReviewState.ChangesRequested))
        {
            reasons.Add("a reviewer requested changes");
        }

        // Code owners: required by the protection — and, when the protection can't be read, whenever the repo has a
        // CODEOWNERS file (fail closed: an unknown rule is treated as the stricter one).
        if (facts.CodeOwners is { } owners && (p.RequireCodeOwnerReviews || (p.Protected && !p.DetailsKnown)))
        {
            var missing = owners.UnapprovedFiles(facts.ChangedFiles, facts.ApprovedOwners);
            if (missing.Count > 0)
            {
                var first = missing[0];
                reasons.Add($"a code owner must approve {first.Path} ({string.Join(", ", first.Owners)})"
                    + (missing.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" and {missing.Count - 1} more file(s)") : string.Empty));
            }
        }

        var byName = facts.Checks.ToLookup(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var required in p.RequiredChecks.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var runs = byName[required].ToList();
            if (runs.Count == 0) { reasons.Add($"the required check '{required}' has not reported"); }
            else if (!runs.All(r => r.Passed)) { reasons.Add($"the required check '{required}' has not passed"); }
        }
        return reasons;
    }

    /// <summary>The sentence a refused merge is reported with, or null when it may go ahead.</summary>
    public static string? Refusal(MergeFacts facts)
    {
        var reasons = Evaluate(facts);
        return reasons.Count == 0 ? null : "Refusing to merge: " + string.Join("; ", reasons) + ".";
    }
}

/// <summary>A parsed CODEOWNERS file (GitHub semantics: the LAST matching pattern wins; a pattern without owners clears
/// ownership).</summary>
public sealed class CodeOwners
{
    /// <summary>Where GitHub looks for the file, in order (the first one found is used).</summary>
    public static IReadOnlyList<string> Locations { get; } = [".github/CODEOWNERS", "CODEOWNERS", "docs/CODEOWNERS"];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private readonly List<(Regex Pattern, IReadOnlyList<string> Owners)> _rules;

    private CodeOwners(List<(Regex, IReadOnlyList<string>)> rules) => _rules = rules;

    /// <summary>How many rules the file has.</summary>
    public int Count => _rules.Count;

    /// <summary>Parse the file's text (comments, blank lines and unparsable patterns are skipped).</summary>
    public static CodeOwners Parse(string? text)
    {
        var rules = new List<(Regex, IReadOnlyList<string>)>();
        foreach (var line in (text ?? string.Empty).Split('\n').Select(WithoutComment))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].StartsWith('!')) { continue; }
            var owners = parts.Skip(1).Where(o => o.Contains('@', StringComparison.Ordinal)).Select(o => o.ToLowerInvariant()).ToList();
            rules.Add((ToRegex(parts[0]), owners));
        }
        return new CodeOwners(rules);
    }

    // A line trimmed and without its comment ("#" at the start, or " #" after the owners); a comment line reads empty.
    private static string WithoutComment(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#') { return string.Empty; }
        var hash = line.IndexOf(" #", StringComparison.Ordinal);
        return hash > 0 ? line[..hash] : line;
    }

    /// <summary>The owners of <paramref name="path"/> (lower case; empty when nobody owns it).</summary>
    public IReadOnlyList<string> OwnersOf(string path)
    {
        var p = (path ?? string.Empty).TrimStart('/');
        for (var i = _rules.Count - 1; i >= 0; i--)
        {
            try
            {
                if (_rules[i].Pattern.IsMatch(p)) { return _rules[i].Owners; }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological pattern: treat it as matching (owned) — the stricter reading.
                return _rules[i].Owners;
            }
        }
        return [];
    }

    /// <summary>The changed files that have owners of which none is in <paramref name="approvedOwners"/>, with their owners.</summary>
    public IReadOnlyList<(string Path, IReadOnlyList<string> Owners)> UnapprovedFiles(IEnumerable<string> changedFiles, IReadOnlySet<string> approvedOwners)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(approvedOwners);
        var missing = new List<(string, IReadOnlyList<string>)>();
        foreach (var file in changedFiles.Distinct(StringComparer.Ordinal))
        {
            var owners = OwnersOf(file);
            if (owners.Count > 0 && !owners.Any(approvedOwners.Contains)) { missing.Add((file, owners)); }
        }
        return missing;
    }

    /// <summary>Every owner token the file names (lower case).</summary>
    public IReadOnlySet<string> AllOwners() => _rules.SelectMany(r => r.Owners).ToHashSet(StringComparer.Ordinal);

    // gitignore-style pattern → anchored regex: "/x" and "a/b" are relative to the root, "x" matches at any depth; "*" stays
    // inside a directory, "**" crosses them; "dir/" owns everything below; "dir/*" only the files directly in it.
    internal static Regex ToRegex(string pattern)
    {
        var p = pattern;
        var anchored = p.StartsWith('/') || p.TrimEnd('/').Contains('/', StringComparison.Ordinal);
        p = p.TrimStart('/');
        var directOnly = p.EndsWith("/*", StringComparison.Ordinal);
        if (p.EndsWith('/')) { p += "**"; }
        var body = new StringBuilder();
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                var slashAfter = i + 2 < p.Length && p[i + 2] == '/';
                body.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*') { body.Append("[^/]*"); }
            else if (c == '?') { body.Append("[^/]"); }
            else { body.Append(Regex.Escape(c.ToString())); }
        }
        var regex = (anchored ? "^" : "^(?:.*/)?") + body + (directOnly ? "$" : "(?:/.*)?$");
        return new Regex(regex, RegexOptions.CultureInvariant, MatchTimeout);
    }
}
