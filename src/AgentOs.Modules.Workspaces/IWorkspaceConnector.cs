// M2 / board reshape — the connect-a-board flow + add-a-repo flow, shared by the HTTP endpoints
// (tenant from ITenantContext) and the desktop Board app (tenant from the signed-in principal — a
// circuit has no HttpContext). Connect validates the board via the source provider, stores the token
// ONLY in the encrypted AppConfig store under the board's CredentialRef, and persists the row (which
// never carries the secret). Adding a repo validates the repo with the board's stored token, then
// persists a child row. Repo validation stays server-side so the UI never has to.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;

namespace AgentOs.Modules.Workspaces;

/// <summary>Connect-a-board input. <see cref="AccessToken"/> is validated, stored encrypted, and never
/// persisted on the row or returned. A board (GitHub Projects v2 / ADO board) spans many repos added
/// separately via <see cref="IWorkspaceConnector.AddRepoAsync"/>. With a <see cref="Tracker"/> (E1.8) the board's tickets
/// come from Azure DevOps Boards or Jira instead; <see cref="Kind"/> + <see cref="AccessToken"/> stay the CODE host's.</summary>
public sealed record WorkspaceConnectInput(
    string Name,
    SourceProviderKind Kind,
    string ProjectOwner,
    string ProjectScope,
    int? ProjectNumber,
    string? Project,
    string? Host,
    string AccessToken,
    TrackerConnectInput? Tracker = null);

/// <summary>E1.8 — where a board's tickets come from when it isn't its code host's own board.</summary>
/// <param name="Binding">The tracker coordinates, filter and workflow states.</param>
/// <param name="AccessToken">The tracker's PAT (Azure DevOps) or API token (Jira) — validated, then stored encrypted. Blank
/// reuses the code host's token when both are the same Azure DevOps organization.</param>
public sealed record TrackerConnectInput(TrackerBinding Binding, string? AccessToken);

/// <summary>Outcome of a connect attempt. On failure <see cref="Error"/> is a user-facing message.</summary>
public sealed record WorkspaceConnectResult(bool Ok, WorkspaceEntity? Workspace, string? Error)
{
    public static WorkspaceConnectResult Fail(string error) => new(false, null, error);
    public static WorkspaceConnectResult Success(WorkspaceEntity workspace) => new(true, workspace, null);
}

/// <summary>Outcome of adding a repo under a board.</summary>
public sealed record RepoAddResult(bool Ok, WorkspaceRepoEntity? Repo, string? Error)
{
    public static RepoAddResult Fail(string error) => new(false, null, error);
    public static RepoAddResult Success(WorkspaceRepoEntity repo) => new(true, repo, null);
}

/// <summary>Validates + persists a connected board and the repos under it, for an explicit tenant.</summary>
public interface IWorkspaceConnector
{
    /// <summary>Connect a planning board. A board number is optional — without one it is a repos-only board (no tickets).</summary>
    Task<WorkspaceConnectResult> ConnectAsync(
        string tenantId, string? userId, WorkspaceConnectInput input, CancellationToken ct = default);

    /// <summary>Validate a repo with the board's stored token and connect it under the board.</summary>
    Task<RepoAddResult> AddRepoAsync(
        string tenantId, Guid workspaceId, string owner, string repo, string? defaultBranch, CancellationToken ct = default);

    /// <summary>List the Projects-v2 / ADO boards the supplied token can see — drives the "Find boards"
    /// picker so the user selects a board instead of typing its number. In-proc seam for the desktop
    /// Board app (the equivalent REST endpoint lives on the API host, which the Web circuit can't call).</summary>
    Task<IReadOnlyList<BoardSummary>> ListBoardsAsync(
        SourceProviderKind kind, string owner, string token, string? host = null, CancellationToken ct = default);
}

internal sealed class WorkspaceConnector : IWorkspaceConnector
{
    private readonly IWorkspaceRepository _repo;
    private readonly ISourceProviderResolver _providers;
    private readonly IAppConfigStore _credentials;
    private readonly TimeProvider _clock;
    private readonly Security.IWorkspaceHostPolicy _hostPolicy;

    public WorkspaceConnector(
        IWorkspaceRepository repo, ISourceProviderResolver providers, IAppConfigStore credentials,
        TimeProvider clock, Security.IWorkspaceHostPolicy hostPolicy)
    {
        _repo = repo;
        _providers = providers;
        _credentials = credentials;
        _clock = clock;
        _hostPolicy = hostPolicy;
    }

    public async Task<WorkspaceConnectResult> ConnectAsync(
        string tenantId, string? userId, WorkspaceConnectInput input, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (input is null || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.AccessToken))
        {
            return WorkspaceConnectResult.Fail("Name and access token are required.");
        }
        if (!_providers.TryResolve(input.Kind, out var provider) || provider is null)
        {
            return WorkspaceConnectResult.Fail($"No source provider registered for '{input.Kind}'.");
        }
        if (!_hostPolicy.IsAllowed(input.Host))
        {
            return WorkspaceConnectResult.Fail(
                $"Host '{input.Host}' is not on the allowed-hosts list. Add it under Workspaces:AllowedHosts to connect a self-hosted server.");
        }

        if (input.Kind == SourceProviderKind.Jira)
        {
            return WorkspaceConnectResult.Fail("Jira holds tickets, not code — pick GitHub or Azure Repos for the code, and Jira under \"Tickets from\".");
        }

        var id = Guid.NewGuid();
        (TrackerBinding? Binding, string? Token, string? Error)? tracker = null;
        if (input.Tracker is not null) { tracker = await ValidateTrackerAsync(id, tenantId, input, ct).ConfigureAwait(false); }
        if (tracker is { Error: { } trackerError }) { return WorkspaceConnectResult.Fail(trackerError); }
        var scope = string.IsNullOrWhiteSpace(input.ProjectScope) ? "user" : input.ProjectScope;
        var board = new BoardDescriptor(
            id, tenantId, input.Kind, input.ProjectOwner, scope, input.ProjectNumber, null,
            input.AccessToken, input.Project, input.Host);
        try
        {
            board.Validate();
        }
        catch (ArgumentException ex)
        {
            return WorkspaceConnectResult.Fail(ex.Message);
        }

        // A board number is optional. With one, validate it (and cache the node id); without one this
        // is a repos-only board whose token is first exercised when a repo is added.
        string? nodeId = null;
        if (input.ProjectNumber is not null && tracker is null)
        {
            var validation = await provider.ValidateBoardAsync(board, ct).ConfigureAwait(false);
            if (!validation.Ok)
            {
                return WorkspaceConnectResult.Fail(
                    validation.Error ?? "Could not reach the board with the supplied credentials.");
            }
            nodeId = validation.NodeId;
        }

        var credentialRef = CredentialKey(id);
        await _credentials.SetForTenantAsync(tenantId, credentialRef, input.AccessToken, ct).ConfigureAwait(false);
        if (tracker is { Binding: not null, Token: { } trackerToken })
        {
            await _credentials.SetForTenantAsync(tenantId, TrackerBinding.TokenKey(id), trackerToken, ct).ConfigureAwait(false);
        }

        var entity = new WorkspaceEntity
        {
            Id = id,
            TenantId = tenantId,
            Name = input.Name,
            Kind = input.Kind,
            ProjectOwner = input.ProjectOwner,
            ProjectScope = scope,
            ProjectNumber = tracker is null ? input.ProjectNumber : null,
            ProjectNodeId = nodeId,
            Tracker = tracker?.Binding?.Serialize(),
            Project = input.Project,
            CredentialRef = credentialRef,
            CreatedByUserId = userId,
            CreatedAtUtc = _clock.GetUtcNow(),
            Status = "Connected",
        };
        await _repo.AddForTenantAsync(entity, ct).ConfigureAwait(false);
        return WorkspaceConnectResult.Success(entity);
    }

    public async Task<IReadOnlyList<BoardSummary>> ListBoardsAsync(
        SourceProviderKind kind, string owner, string token, string? host = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(owner))
        {
            return [];
        }
        if (!_providers.TryResolve(kind, out var provider) || provider is null)
        {
            return [];
        }
        if (!_hostPolicy.IsAllowed(host))
        {
            return [];
        }
        var creds = new ConnectionCredentials(kind, token, owner, host);
        return await provider.ListBoardsAsync(creds, ct).ConfigureAwait(false);
    }

    public async Task<RepoAddResult> AddRepoAsync(
        string tenantId, Guid workspaceId, string owner, string repo, string? defaultBranch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            return RepoAddResult.Fail("Owner and repository are required.");
        }

        var board = await _repo.GetForTenantAsync(tenantId, workspaceId, ct).ConfigureAwait(false);
        if (board is null)
        {
            return RepoAddResult.Fail("Board not found.");
        }
        if (!_providers.TryResolve(board.Kind, out var provider) || provider is null)
        {
            return RepoAddResult.Fail($"No source provider registered for '{board.Kind}'.");
        }

        var pat = await _credentials.GetForTenantAsync(tenantId, board.CredentialRef, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(pat))
        {
            return RepoAddResult.Fail("Stored credentials for this board are missing; reconnect it.");
        }

        var requestedBranch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch!;
        var descriptor = new WorkspaceDescriptor(
            workspaceId, tenantId, board.Kind, owner, repo, board.Project, requestedBranch, pat);
        try
        {
            descriptor.Validate();
        }
        catch (ArgumentException ex)
        {
            return RepoAddResult.Fail(ex.Message);
        }

        var validation = await provider.ValidateAsync(descriptor, ct).ConfigureAwait(false);
        if (!validation.Ok)
        {
            return RepoAddResult.Fail(
                validation.Error ?? "Could not reach the repository with the supplied credentials.");
        }

        var entity = new WorkspaceRepoEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            Owner = owner,
            Repo = repo,
            DefaultBranch = validation.DefaultBranch ?? requestedBranch,
            RemoteUrl = BuildRemoteUrl(board.Kind, owner, board.Project, repo, host: null),
            Private = false,
            AddedAtUtc = _clock.GetUtcNow(),
        };
        await _repo.AddRepoForTenantAsync(entity, ct).ConfigureAwait(false);
        return RepoAddResult.Success(entity);
    }

    // The tracker must be reachable with its token, and its query must run — checked before anything is stored.
    private async Task<(TrackerBinding? Binding, string? Token, string? Error)> ValidateTrackerAsync(
        Guid id, string tenantId, WorkspaceConnectInput input, CancellationToken ct)
    {
        var binding = Normalize(input.Tracker!.Binding);
        if (binding.Problem() is { } problem) { return (null, null, problem); }
        var token = string.IsNullOrWhiteSpace(input.Tracker.AccessToken) ? null : input.Tracker.AccessToken.Trim();
        if (token is null)
        {
            // Same Azure DevOps organization for code and tickets: one PAT serves both.
            var sameOrg = binding.Kind == SourceProviderKind.AzureDevOps && input.Kind == SourceProviderKind.AzureDevOps
                && string.Equals(binding.Site, input.ProjectOwner?.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!sameOrg) { return (null, null, binding.Kind == SourceProviderKind.Jira ? "The Jira API token is required." : "The Azure DevOps PAT for the tickets is required."); }
            token = input.AccessToken;
        }
        var host = binding.Kind == SourceProviderKind.Jira ? binding.JiraBaseUrl() : binding.Host;
        if (!_hostPolicy.IsAllowed(host))
        {
            return (null, null, $"Host '{host}' is not on the allowed-hosts list. Add it under Workspaces:AllowedHosts to connect a self-hosted tracker.");
        }
        if (!_providers.TryResolve(binding.Kind, out var provider) || provider is null)
        {
            return (null, null, $"No ticket tracker registered for '{binding.Kind}'.");
        }
        var probe = new BoardDescriptor(id, tenantId, binding.Kind, binding.Site, "org", null, null, token, binding.Project, binding.Host)
        {
            Tracker = binding,
        };
        var validation = await provider.ValidateBoardAsync(probe, ct).ConfigureAwait(false);
        return validation.Ok ? (binding, token, null) : (null, null, validation.Error ?? "Could not reach the ticket tracker with the supplied credentials.");
    }

    // Trimmed coordinates; a Jira site as its https origin; the default workflow states when none were given.
    internal static TrackerBinding Normalize(TrackerBinding b)
    {
        var (start, done) = TrackerBinding.DefaultStates(b.Kind);
        var site = b.Site?.Trim() ?? string.Empty;
        var normalized = b with
        {
            Site = site,
            Project = b.Project?.Trim() ?? string.Empty,
            Account = string.IsNullOrWhiteSpace(b.Account) ? null : b.Account.Trim(),
            Filter = b.Filter ?? TrackerFilter.None,
            StartState = b.StartState is null ? start : b.StartState.Trim(),
            DoneState = b.DoneState is null ? done : b.DoneState.Trim(),
            Host = string.IsNullOrWhiteSpace(b.Host) ? null : b.Host.Trim(),
        };
        return b.Kind == SourceProviderKind.Jira && normalized.JiraBaseUrl() is { } origin ? normalized with { Site = origin } : normalized;
    }

    internal static string CredentialKey(Guid id) =>
        string.Create(CultureInfo.InvariantCulture, $"workspace/{id:N}/token");

    internal static string BuildRemoteUrl(SourceProviderKind kind, string owner, string? project, string repo, string? host)
    {
        var baseHost = string.IsNullOrWhiteSpace(host)
            ? (kind == SourceProviderKind.AzureDevOps ? "https://dev.azure.com" : "https://github.com")
            : host!.TrimEnd('/');

        return kind == SourceProviderKind.AzureDevOps
            ? string.Create(CultureInfo.InvariantCulture, $"{baseHost}/{owner}/{project}/_git/{repo}")
            : string.Create(CultureInfo.InvariantCulture, $"{baseHost}/{owner}/{repo}");
    }
}
