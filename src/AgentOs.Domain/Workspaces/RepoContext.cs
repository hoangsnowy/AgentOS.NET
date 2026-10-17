// M2 — a lightweight read of a connected repo used to GROUND the Requirement agent: the README plus
// the top-level layout give the agent project context without cloning. Kept small on purpose; deep
// file reads happen later inside a remote session (M4), through the governed tool gateway.

using System.Collections.Generic;

namespace AgentOs.Domain.Workspaces;

/// <summary>Repo context for grounding the Requirement agent. Returned by <see cref="ISourceProvider.ReadRepoContextAsync"/>.</summary>
/// <param name="FullName">Display path of the repo.</param>
/// <param name="DefaultBranch">Resolved default branch.</param>
/// <param name="Description">Repo description, if any.</param>
/// <param name="Readme">Decoded README text (may be empty).</param>
/// <param name="TopLevelPaths">Root-level file/dir names — a cheap layout hint.</param>
public sealed record RepoContext(
    string FullName,
    string DefaultBranch,
    string? Description,
    string Readme,
    IReadOnlyList<string> TopLevelPaths)
{
    /// <summary>Agent instruction files found in the repo (AGENTS.md, CLAUDE.md, …), already budgeted.</summary>
    public IReadOnlyList<RepoInstructionFile> Instructions { get; init; } = [];
}

/// <summary>Outcome of validating a connection's credentials + repo existence.</summary>
/// <param name="Ok">True when the token can reach the repo.</param>
/// <param name="DefaultBranch">Resolved default branch when <see cref="Ok"/>.</param>
/// <param name="Error">Human-readable reason when not <see cref="Ok"/>.</param>
public sealed record RepoValidation(bool Ok, string? DefaultBranch = null, string? Error = null)
{
    /// <summary>A successful validation carrying the resolved default branch.</summary>
    public static RepoValidation Success(string defaultBranch) => new(true, defaultBranch);

    /// <summary>A failed validation carrying the reason.</summary>
    public static RepoValidation Fail(string error) => new(false, null, error);
}

/// <summary>Credentials for a listing probe (no specific repo chosen yet).</summary>
/// <param name="Kind">Which provider to talk to.</param>
/// <param name="AccessToken">PAT / OAuth token. Transient.</param>
/// <param name="Owner">GitHub owner/org or Azure DevOps organization to scope the listing.</param>
/// <param name="Host">Base host for enterprise/self-hosted; null = public host.</param>
public sealed record ConnectionCredentials(
    SourceProviderKind Kind,
    string AccessToken,
    string? Owner = null,
    string? Host = null);
