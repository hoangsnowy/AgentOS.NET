// Opens a GitHub PR from a PipelineResult. Reads PAT + target repo from IRuntimeOverrides
// (tenant-scoped — each tenant carries its own credentials). GitHub Enterprise is supported
// via the GitHubBaseUrl override; null / blank falls back to the public github.com host.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Llm;
using Microsoft.Extensions.Logging;
using Octokit;

namespace AgentOs.Modules.Integration;

/// <inheritdoc cref="IGitHubPrService"/>
public sealed class GitHubPrService : IGitHubPrService
{
    private const string DefaultBranchPrefix = "agentos";

    private readonly IRuntimeOverrides _overrides;
    private readonly ILogger<GitHubPrService> _logger;

    public GitHubPrService(IRuntimeOverrides overrides, ILogger<GitHubPrService> logger)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(logger);
        _overrides = overrides;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<GitHubPrResult> OpenPrAsync(PipelineResult result, string title, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var pat = _overrides.GitHubPat;
        var owner = _overrides.GitHubRepoOwner;
        var name = _overrides.GitHubRepoName;
        var baseBranch = string.IsNullOrWhiteSpace(_overrides.GitHubBaseBranch) ? "main" : _overrides.GitHubBaseBranch!;
        var baseUrl = _overrides.GitHubBaseUrl;

        var missing = new List<string>(3);
        if (string.IsNullOrWhiteSpace(pat)) { missing.Add("GitHubPat"); }
        if (string.IsNullOrWhiteSpace(owner)) { missing.Add("GitHubRepoOwner"); }
        if (string.IsNullOrWhiteSpace(name)) { missing.Add("GitHubRepoName"); }
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"GitHub integration is not configured for this tenant. Missing: {string.Join(", ", missing)}. " +
                "Set these on the Settings page, or pick a connected workspace repo.");
        }

        ArgumentNullException.ThrowIfNull(result);
        EnsureGate(result);
        return OpenPrCoreAsync(owner!, name!, baseBranch, pat!, baseUrl, result, title, body, ct);
    }

    // The QA gate (plan 0.5) at the single PR seam: no caller can open a PR for a result QA didn't pass.
    private void EnsureGate(PipelineResult result)
    {
        var gate = PrGate.Evaluate(result);
        if (!gate.Allowed)
        {
            _logger.LogWarning("PR blocked by the QA gate: {Reason}", gate.Reason);
            throw new PrGateBlockedException(gate.Reason!);
        }
    }

    /// <inheritdoc />
    public Task<GitHubPrResult> OpenPrAsync(PipelineResult result, WorkspaceDescriptor workspace, string title, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.Validate();
        if (workspace.Kind != SourceProviderKind.GitHub)
        {
            throw new InvalidOperationException(
                $"Opening a PR from a pipeline result is GitHub-only; the selected workspace is '{workspace.Kind}'.");
        }

        EnsureGate(result);
        var baseBranch = string.IsNullOrWhiteSpace(workspace.DefaultBranch) ? "main" : workspace.DefaultBranch;
        return OpenPrCoreAsync(workspace.Owner, workspace.Repo, baseBranch, workspace.AccessToken, workspace.Host, result, title, body, ct);
    }

    // Shared core: branch off base, commit the generated files, open the PR. Both overloads funnel here so
    // the Settings-token path and the workspace path differ ONLY in where (owner, repo, branch, token, host)
    // come from.
    private async Task<GitHubPrResult> OpenPrCoreAsync(
        string owner, string name, string baseBranch, string token, string? host,
        PipelineResult result, string title, string body, CancellationToken ct)
    {
        // Never push canned offline output to a real repository: it is placeholder text produced without a
        // model (no API key + Llm:OfflineFallback). Both overloads funnel here, so every caller is covered.
        if (result.IsOffline)
        {
            throw new InvalidOperationException(
                "Refusing to open a PR: this run was answered by the offline canned client (no LLM API key configured), "
                + "so the code is placeholder output, not model-generated. Add a provider key in Settings → LLM and re-run.");
        }

        var client = GitHubClientFactory.Create(token, host);

        // 1. Locate base branch SHA.
        ct.ThrowIfCancellationRequested();
        Reference baseRef;
        try
        {
            baseRef = await client.Git.Reference.Get(owner, name, $"heads/{baseBranch}").ConfigureAwait(false);
        }
        catch (NotFoundException ex)
        {
            throw new InvalidOperationException(
                $"GitHub base branch '{baseBranch}' not found on {owner}/{name}. Set GitHubBaseBranch on Settings or create the branch first.", ex);
        }
        catch (AuthorizationException ex)
        {
            throw new InvalidOperationException(
                $"GitHub rejected the PAT for {owner}/{name}. Check the token's scopes (needs 'repo') and that it isn't expired.", ex);
        }
        var baseSha = baseRef.Object.Sha;

        // 2. Create a new timestamped branch off the base. Prefix is hardcoded `agentos/` so
        // operators can clean up auto-generated branches with a single glob.
        var branch = $"{DefaultBranchPrefix}/{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        ct.ThrowIfCancellationRequested();
        await client.Git.Reference.Create(owner, name, new NewReference($"refs/heads/{branch}", baseSha)).ConfigureAwait(false);
        _logger.LogInformation("Created branch {Branch} on {Owner}/{Name}", branch, owner, name);

        // 3. Commit every generated file.
        var files = new List<(string Path, string Content)>();
        if (result.Code?.Files is not null)
        {
            files.AddRange(result.Code.Files.Select(f => (f.Path, f.Content)));
        }
        if (result.Tests?.Files is not null)
        {
            files.AddRange(result.Tests.Files.Select(f => (f.Path, f.Content)));
        }

        foreach (var (path, content) in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await client.Repository.Content.CreateFile(owner, name, path,
                    new CreateFileRequest($"feat: add {path}", content, branch)).ConfigureAwait(false);
            }
            catch (ApiValidationException)
            {
                // File exists on the branch — fetch SHA and update.
                var existing = await client.Repository.Content.GetAllContentsByRef(owner, name, path, branch).ConfigureAwait(false);
                if (existing.Count > 0)
                {
                    await client.Repository.Content.UpdateFile(owner, name, path,
                        new UpdateFileRequest($"feat: update {path}", content, existing[0].Sha, branch)).ConfigureAwait(false);
                }
            }
        }

        // 4. Open the PR.
        ct.ThrowIfCancellationRequested();
        var pr = await client.PullRequest.Create(owner, name, new NewPullRequest(title, branch, baseBranch)
        {
            Body = body,
        }).ConfigureAwait(false);

        _logger.LogInformation("Opened PR #{Number} ({Url})", pr.Number, pr.HtmlUrl);
        return new GitHubPrResult(pr.Number, pr.HtmlUrl, branch);
    }
}
