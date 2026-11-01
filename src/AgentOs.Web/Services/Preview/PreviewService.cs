// "Try the app": a person runs a project's latest version on their own paired computer and clicks through it, without
// a terminal. The project declares how in `.agentos/preview.json` (PreviewRecipe); this service reads it to show the
// command first, asks the person's runner to run it, and keeps each repo's preview state for the Board — and for
// Autopilot, which holds back acceptance testing on a board while one of its previews holds the computer's ports.
//
// In memory on purpose: the runner is the source of truth. A preview runs outside the runner's process tree, so it
// outlives a runner restart (an update, a re-pairing, a crash); the runner keeps its state on disk and re-announces it
// on every reconnect, and this service re-attaches it — the same card, state and URL. That also rebuilds the Board
// after a Web restart: the announcement carries the board + repo the preview was started for. What a runner announces
// is never trusted as such: a rebuilt card needs a repo of that board in the tenant's own workspace store (owner/repo
// matching) and a local http(s) url, or the preview is refused and the runner is asked to stop it.

using System.Collections.Concurrent;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Preview;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Integration;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;

namespace AgentOs.Web.Services.Preview;

/// <summary>A repo's preview as the Board shows it.</summary>
public sealed record PreviewState(string Id, string TenantId, Guid BoardId, Guid RepoId, string Repo, string Command, string Url,
    string State, string? Message, DateTimeOffset StartedAt, string UserId)
{
    public bool IsActive => PreviewStates.IsActive(State);
}

/// <summary>What reading a repo's recipe found.</summary>
public sealed record RecipeLookup(PreviewRecipe? Recipe, string? Problem, bool Missing)
{
    public static RecipeLookup Found(PreviewRecipe recipe) => new(recipe, null, false);
    public static RecipeLookup NotThere() => new(null, null, true);
    public static RecipeLookup Broken(string problem) => new(null, problem, false);
}

/// <summary>Outcome of asking for a preview.</summary>
public enum PreviewStartResult { Started, NoComputer }

/// <summary>Runs and tracks "Try the app" previews. Singleton.</summary>
public sealed class PreviewService : IDisposable
{
    private readonly IRunnerPreviewBroker _broker;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PreviewService>? _log;
    private readonly ConcurrentDictionary<(string Tenant, Guid Repo), PreviewState> _byRepo = new();
    private readonly ConcurrentDictionary<string, (string Tenant, Guid Repo)> _byId = new(StringComparer.Ordinal);

    public PreviewService(IRunnerPreviewBroker broker, IServiceScopeFactory scopes, ILogger<PreviewService>? log = null)
    {
        _broker = broker;
        _scopes = scopes;
        _log = log;
        _broker.PreviewReported += OnReported;
        _broker.PreviewReattached += OnReattached;
    }

    /// <summary>Raised with the tenant id whenever one of its previews changes.</summary>
    public event Action<string>? Changed;

    /// <summary>The preview of a repo, if one was started (also a finished one, until the next start).</summary>
    public PreviewState? Get(string tenantId, Guid repoId) => _byRepo.GetValueOrDefault((tenantId, repoId));

    /// <summary>True while a preview of one of the board's repos holds a computer — Autopilot waits with testing.</summary>
    public bool IsActiveOnBoard(string tenantId, Guid boardId) =>
        _byRepo.Values.Any(p => p.TenantId == tenantId && p.BoardId == boardId && p.IsActive);

    /// <summary>Read the repo's recipe from its default branch (what the person is shown before anything runs).</summary>
    public async Task<RecipeLookup> ReadRecipeAsync(string tenantId, WorkspaceEntity board, WorkspaceRepoEntity repo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(repo);
        using var scope = _scopes.CreateScope();
        var pat = await scope.ServiceProvider.GetRequiredService<IAppConfigStore>()
            .GetForTenantAsync(tenantId, board.CredentialRef, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(pat)) { return RecipeLookup.Broken("AgentOS.NET has no access to this project's code — reconnect the board."); }
        try
        {
            var text = await scope.ServiceProvider.GetRequiredService<IRepoFileBrowser>()
                .GetTextAsync(repo.Owner, repo.Repo, PreviewRecipe.FilePath, Branch(repo), pat, ct).ConfigureAwait(false);
            var recipe = PreviewRecipe.TryParse(text, out var error);
            return recipe is null ? RecipeLookup.Broken(error ?? "The recipe can't be used.") : RecipeLookup.Found(recipe);
        }
        catch (Octokit.NotFoundException)
        {
            return RecipeLookup.NotThere();
        }
        catch (Exception ex) when (ex is Octokit.ApiException or HttpRequestException or TimeoutException)
        {
            return RecipeLookup.Broken($"Couldn't read the project ({ex.Message}).");
        }
    }

    /// <summary>Run <paramref name="recipe"/> for <paramref name="repo"/> on <paramref name="userId"/>'s computer.</summary>
    public async Task<PreviewStartResult> StartAsync(string tenantId, string userId, Guid boardId, WorkspaceRepoEntity repo, PreviewRecipe recipe,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(recipe);
        // Read before touching any state: from here on nothing awaits, so a start can't interleave with another one.
        var idleMinutes = await IdleMinutesAsync(tenantId, ct).ConfigureAwait(false);
        if (Get(tenantId, repo.Id) is { IsActive: true } running) { _broker.StopPreview(running.Id); }
        var id = Guid.NewGuid().ToString("N");
        var state = new PreviewState(id, tenantId, boardId, repo.Id, $"{repo.Owner}/{repo.Repo}", recipe.Command, recipe.Url.AbsoluteUri,
            PreviewStates.Preparing, "Asking your computer…", DateTimeOffset.UtcNow, userId);
        _byRepo[(tenantId, repo.Id)] = state;
        _byId[id] = (tenantId, repo.Id);
        var start = new PreviewStart(id, repo.Owner, repo.Repo, Branch(repo), recipe.Command, recipe.Url.AbsoluteUri, recipe.ReadyTimeoutSeconds,
            idleMinutes, boardId, repo.Id);
        if (!_broker.StartPreview(start, new RunnerTarget(tenantId, userId)))
        {
            _byRepo.TryRemove((tenantId, repo.Id), out _);
            _byId.TryRemove(id, out _);
            return PreviewStartResult.NoComputer;
        }
        Changed?.Invoke(tenantId);
        return PreviewStartResult.Started;
    }

    /// <summary>Stop a repo's preview (the person pressed Stop).</summary>
    public void Stop(string tenantId, Guid repoId)
    {
        if (Get(tenantId, repoId) is { IsActive: true } running) { _broker.StopPreview(running.Id); }
    }

    /// <summary>Open a ticket asking the AI to add the recipe — a project without one gets it the same way as any change.</summary>
    public async Task<int?> RequestRecipeAsync(string tenantId, WorkspaceEntity board, WorkspaceRepoEntity repo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(repo);
        using var scope = _scopes.CreateScope();
        var pat = await scope.ServiceProvider.GetRequiredService<IAppConfigStore>()
            .GetForTenantAsync(tenantId, board.CredentialRef, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(pat)) { return null; }
        var boardDesc = new BoardDescriptor(board.Id, tenantId, board.Kind, board.ProjectOwner, board.ProjectScope,
            board.ProjectNumber, board.ProjectNodeId, pat, board.Project);
        var repoDesc = BoardTasks.BoardTaskService.RepoDescriptor(board, tenantId, repo.Owner, repo.Repo, Branch(repo), pat);
        var created = await scope.ServiceProvider.GetRequiredService<IBoardWriteService>()
            .CreateTicketsAsync(boardDesc, repoDesc, [RecipeTicket(repo)], ct).ConfigureAwait(false);
        return created.Count > 0 ? created[0].Number : null;
    }

    /// <summary>The set-up ticket: what the recipe is for, its shape, and how to check it.</summary>
    public static TicketDraft RecipeTicket(WorkspaceRepoEntity repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var body = $"""
            People want to try this project on their own computer from AgentOS.NET (Board › Projects › Try the app), without
            a terminal. Add the recipe that says how to run it locally: `{PreviewRecipe.FilePath}` at the repository root.

            ```json
            {PreviewRecipe.Example}
            ```

            - `command`: ONE shell line run from the repository root that starts the whole app for local use (for an
              Aspire solution, its AppHost). It must keep running while the app is up and must not ask questions.
            - `url`: the page a person opens first, on localhost.
            - `readyTimeoutSeconds` (optional, 10–3600): how long the first start may take (restores, container pulls).

            ## Acceptance criteria
            - [ ] `{PreviewRecipe.FilePath}` exists, is valid JSON, and has `command` and a localhost `url`.
            - [ ] From a fresh clone, running `command` from the repository root starts the app and `url` answers within
                  `readyTimeoutSeconds`.
            - [ ] The README mentions the recipe in its local-run section.
            """;
        return new TicketDraft($"Add a \"Try the app\" recipe ({PreviewRecipe.FilePath})", body,
            ["type:chore", "p1", StandardLabels.AiReady], true);
    }

    private void OnReported(PreviewStatus status)
    {
        if (!_byId.TryGetValue(status.PreviewId, out var key)) { return; }
        if (!_byRepo.TryGetValue(key, out var current) || current.Id != status.PreviewId) { return; }
        _byRepo[key] = current with { State = status.State, Message = status.Message };
        if (!PreviewStates.IsActive(status.State)) { _byId.TryRemove(status.PreviewId, out _); }
        Changed?.Invoke(key.Tenant);
    }

    private const string StillStarting = "Your computer reconnected — the app is still starting.";

    // A runner (re)connected and still runs this preview. Known → the same card carries on (with the url the Board itself
    // started it with); unknown (the Web restarted) → rebuilt from what the runner kept, but only after checking it.
    private void OnReattached(PreviewReattach reattach)
    {
        var p = reattach.Preview;
        if (_byId.TryGetValue(p.PreviewId, out var key) && _byRepo.TryGetValue(key, out var current) && current.Id == p.PreviewId)
        {
            _byRepo[key] = current with { State = p.State, Message = p.State == PreviewStates.Ready ? current.Url : StillStarting };
            Changed?.Invoke(key.Tenant);
            return;
        }
        _ = AttachUnknownAsync(reattach);
    }

    // The runner's record is the runner's word only. It goes on the Board when (1) its url is a local http(s) address — it
    // ends up behind "Open the app" — and (2) its repo is one of that board's repos in this tenant's own workspace store,
    // with the same owner/repo. Anything else is refused and stopped, so nothing keeps running unseen on the computer.
    private async Task AttachUnknownAsync(PreviewReattach reattach)
    {
        var p = reattach.Preview;
        WorkspaceRepoEntity? repo = null;
        if (p.BoardId != Guid.Empty && p.RepoId != Guid.Empty && PreviewRecipe.IsLocalHttpUrl(p.Url))
        {
            try { repo = await FindRepoAsync(reattach.TenantId, p).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex, "Couldn't check a re-announced preview against the workspace store; stopping it");
            }
        }
        var repoKey = (reattach.TenantId, p.RepoId);
        if (repo is null || (_byRepo.TryGetValue(repoKey, out var other) && other.Id != p.PreviewId && other.IsActive))
        {
            if (repo is null) { _log?.LogWarning("Refused a re-announced preview the Board can't place (board, repo or url don't check out); asking the runner to stop it"); }
            _broker.StopPreview(p.PreviewId);
            return;
        }
        _byRepo[repoKey] = new PreviewState(p.PreviewId, reattach.TenantId, repo.WorkspaceId, repo.Id, $"{repo.Owner}/{repo.Repo}", p.Command, p.Url,
            p.State, p.State == PreviewStates.Ready ? p.Url : StillStarting, p.StartedAt, reattach.UserId);
        _byId[p.PreviewId] = repoKey;
        Changed?.Invoke(reattach.TenantId);
    }

    // The tenant's repo the preview names: under that board, in that tenant, with the same owner/repo. Null otherwise.
    private async Task<WorkspaceRepoEntity?> FindRepoAsync(string tenantId, PreviewAnnouncement p)
    {
        using var scope = _scopes.CreateScope();
        if (scope.ServiceProvider.GetService<IWorkspaceRepository>() is not { } store) { return null; }
        var repos = await store.ListReposForTenantAsync(tenantId, p.BoardId).ConfigureAwait(false);
        return repos.FirstOrDefault(r => r.Id == p.RepoId && r.WorkspaceId == p.BoardId
            && string.Equals(r.TenantId, tenantId, StringComparison.Ordinal)
            && string.Equals(r.Owner, p.Owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Repo, p.Repo, StringComparison.OrdinalIgnoreCase));
    }

    // The tenant's idle stop (Settings › Autopilot); the built-in value when settings aren't available.
    private async Task<int> IdleMinutesAsync(string tenantId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var settings = scope.ServiceProvider.GetService<ISettings>();
        if (settings is null) { return AutopilotSettings.DefaultPreviewIdleMinutes; }
        return await AutopilotSettings.ReadPreviewIdleMinutesAsync(settings, tenantId, ct).ConfigureAwait(false);
    }

    private static string Branch(WorkspaceRepoEntity repo) => string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;

    public void Dispose()
    {
        _broker.PreviewReported -= OnReported;
        _broker.PreviewReattached -= OnReattached;
    }
}
