// Runs a Board task (a session) end to end — the single implementation shared by the Board UI and the project
// orchestrator. It used to live inside BoardApp.razor, so only a person clicking in an open window could start or
// follow up a task; an orchestrator running on the server could not. The UI now only turns the StartResult into
// words; everything that happens after the click runs here, off any circuit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Integration;
using AgentOs.Modules.Pipeline.Pipeline;
using AgentOs.Modules.Pipeline.Sessions;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentOs.Web.Services.BoardTasks;

/// <summary>Why a task did or didn't start.</summary>
public enum TaskStart
{
    Started,
    TaskNotFound,
    BoardNotFound,
    NoCodeProject,
    NoGitHubAccess,
}

/// <summary>A started task step: why it did or didn't start, and the work itself (completed when nothing started) —
/// awaited by a per-ticket MAF workflow, ignored by fire-and-forget callers.</summary>
public sealed record TaskRun(TaskStart Status, Task Completion)
{
    public static implicit operator TaskRun(TaskStart status) => new(status, Task.CompletedTask);
}

/// <summary>Starts Board tasks and follow-ups. Singleton; every call is tenant-explicit.</summary>
public sealed class BoardTaskService(
    IServiceScopeFactory scopes,
    ISessionRunFeed feed,
    TaskStopRegistry stops,
    IOptions<PipelineOptions> pipelineOptions)
{
    // Acceptance runs at once per member's computer (Settings › Autopilot › Acceptance tests at once), whoever asks.
    private readonly RunGate _testGate = new();

    /// <summary>A repo a new task works in (the first one is the task's primary repo).</summary>
    public sealed record TaskRepo(string Owner, string Repo, string DefaultBranch, Guid? WorkspaceRepoId);

    /// <summary>Create a task (a draft session + its repos) for a ticket, as <paramref name="memberUserId"/>.</summary>
    public async Task<RemoteSessionEntity> CreateAsync(string tenantId, string memberUserId, Guid boardId, int? issueNumber, string title,
        string? itemNodeId, string ticketKind, IReadOnlyList<TaskRepo> repos, bool runOnMachine, string brain = "Quick",
        string? ticketType = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(repos);
        if (repos.Count == 0) { throw new ArgumentException("A task needs at least one repository.", nameof(repos)); }
        using var scope = scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var primary = repos[0];
        var session = new RemoteSessionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WorkspaceId = boardId,
            MemberUserId = memberUserId,
            Title = title,
            Status = "Draft",
            IssueNumber = issueNumber,
            RepoOwner = primary.Owner,
            RepoName = primary.Repo,
            RepoDefaultBranch = primary.DefaultBranch,
            BoardItemNodeId = itemNodeId,
            TicketKind = ticketKind,
            RunOnMachine = runOnMachine,
            Brain = brain,
            TicketType = ticketType,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedByUserId = string.IsNullOrEmpty(memberUserId) ? null : memberUserId,
        };
        await sessions.AddForTenantAsync(session, ct).ConfigureAwait(false);
        foreach (var r in repos)
        {
            await sessions.AddRepoForTenantAsync(new SessionRepoEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SessionId = session.Id,
                WorkspaceRepoId = r.WorkspaceRepoId,
                Owner = r.Owner,
                Repo = r.Repo,
                DefaultBranch = r.DefaultBranch,
                Status = "Pending",
            }, ct).ConfigureAwait(false);
        }
        return session;
    }

    /// <summary>
    /// Start (or restart) a task. With <paramref name="changeRequest"/> it is a follow-up: each repo that already has a
    /// pull request continues on its branch. Returns once the work is under way; the run reports on the session feed.
    /// </summary>
    public async Task<TaskStart> StartAsync(string tenantId, Guid sessionId, string? changeRequest = null, string? cliProfile = null,
        CancellationToken ct = default) =>
        (await BeginStartAsync(tenantId, sessionId, changeRequest, cliProfile, ct).ConfigureAwait(false)).Status;

    /// <summary><see cref="StartAsync"/>, returning the running work too.</summary>
    public async Task<TaskRun> BeginStartAsync(string tenantId, Guid sessionId, string? changeRequest = null, string? cliProfile = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        using var scope = scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var session = (await sessions.ListForTenantAsync(tenantId, ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == sessionId);
        if (session is null) { return TaskStart.TaskNotFound; }
        var board = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>()
            .GetForTenantAsync(tenantId, session.WorkspaceId, ct).ConfigureAwait(false);
        if (board is null) { return TaskStart.BoardNotFound; }
        var children = await sessions.ListReposForTenantAsync(tenantId, session.Id, ct).ConfigureAwait(false);
        if (children.Count == 0) { return TaskStart.NoCodeProject; }
        var pat = await scope.ServiceProvider.GetRequiredService<IAppConfigStore>()
            .GetForTenantAsync(tenantId, board.CredentialRef, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(pat)) { return TaskStart.NoGitHubAccess; }

        await sessions.UpdateRunResultAsync(tenantId, session.Id, "Running", null, null, ct).ConfigureAwait(false);
        // Try again: a repo that failed last time starts over clean (its old error would read as this run's).
        foreach (var failed in children.Where(c => c.Status == "Failed"))
        {
            // A follow-up keeps the branch + pull request it continues; a plain retry starts over.
            var keep = changeRequest is not null && !string.IsNullOrEmpty(failed.PrUrl);
            await sessions.UpdateRepoRunResultAsync(tenantId, failed.Id, "Pending", keep ? failed.BranchName : null, keep ? failed.PrUrl : null, null, ct).ConfigureAwait(false);
            failed.Status = "Pending";
            failed.Error = null;
        }

        var change = string.IsNullOrWhiteSpace(changeRequest) ? null : changeRequest.Trim();
        // Delivery metrics: every change sent back to the Coding agent is one more fix round of this task.
        if (change is not null) { await sessions.IncrementFixRoundsForTenantAsync(tenantId, session.Id, ct).ConfigureAwait(false); }
        if (change is not null)
        {
            // Knowledge graph: what the review / failing checks / acceptance test said becomes a lesson of the project,
            // read back by every later task on it.
            var memory = scope.ServiceProvider.GetRequiredService<AgentOs.Domain.Memory.IMemoryStore>();
            foreach (var c in children.Where(c => !string.IsNullOrEmpty(c.PrUrl)))
            {
                await AgentOs.Domain.Memory.ProjectMemory.RecordLessonAsync(memory, tenantId, $"{c.Owner}/{c.Repo}", c.PrUrl, change, ct).ConfigureAwait(false);
            }
        }
        var stopLease = stops.Begin(tenantId, session.Id);
        // Fire-and-forget in a fresh scope, independent of whoever asked (a circuit, or the orchestrator).
        // The Thorough engine stores its own pipeline run + metrics; every other run is recorded here for Insights.
        var completion = Task.Run(() => EngineSelection.IsQuality(session.Brain)
            ? RunAsync(tenantId, session, board, pat, children, cliProfile, change, stopLease)
            : RecordedAsync(tenantId, session, AgentOs.Modules.Pipeline.Agents.AgentRoles.IssueWork, () => RunAsync(tenantId, session, board, pat, children, cliProfile, change, stopLease)),
            CancellationToken.None);
        return new TaskRun(TaskStart.Started, completion);
    }

    /// <summary>
    /// Agentic Testing: test every open proposed change of a task against its ticket's acceptance criteria on the
    /// member's computer, post the evidence on each pull request, and mark it Accepted or Rejected.
    /// </summary>
    /// <param name="captureDesign">The ticket's flow runs the Design gate (ADR-0016): the run also takes screenshots of the
    /// changed screens, sent back to the task's design scope for its current fix round.</param>
    public async Task<TaskStart> TestAsync(string tenantId, Guid sessionId, string? cliProfile = null, bool captureDesign = false, CancellationToken ct = default) =>
        (await BeginTestAsync(tenantId, sessionId, cliProfile, captureDesign, ct).ConfigureAwait(false)).Status;

    /// <summary><see cref="TestAsync"/>, returning the running work too.</summary>
    public async Task<TaskRun> BeginTestAsync(string tenantId, Guid sessionId, string? cliProfile = null, bool captureDesign = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        using var scope = scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var session = (await sessions.ListForTenantAsync(tenantId, ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == sessionId);
        if (session is null) { return TaskStart.TaskNotFound; }
        var board = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>()
            .GetForTenantAsync(tenantId, session.WorkspaceId, ct).ConfigureAwait(false);
        if (board is null) { return TaskStart.BoardNotFound; }
        var changes = (await sessions.ListReposForTenantAsync(tenantId, session.Id, ct).ConfigureAwait(false))
            .Where(r => r.Status is "Done" or "Reviewed" or "Accepted" or "Rejected" && !string.IsNullOrEmpty(r.PrUrl) && !string.IsNullOrEmpty(r.BranchName))
            .ToList();
        if (changes.Count == 0) { return TaskStart.NoCodeProject; }
        var pat = await scope.ServiceProvider.GetRequiredService<IAppConfigStore>()
            .GetForTenantAsync(tenantId, board.CredentialRef, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(pat)) { return TaskStart.NoGitHubAccess; }

        await sessions.UpdateRunResultAsync(tenantId, session.Id, "Running", session.PrUrl, null, ct).ConfigureAwait(false);
        foreach (var c in changes)
        {
            await sessions.UpdateRepoRunResultAsync(tenantId, c.Id, "Testing", c.BranchName, c.PrUrl, null, ct).ConfigureAwait(false);
        }
        // Design gate: this run's screenshots go to the scope of the task's current change (its fix round), emptied first so
        // a re-test never mixes in an earlier capture.
        DesignCapture? capture = null;
        if (captureDesign && session.IssueNumber is > 0)
        {
            capture = new DesignCapture(DesignScope.For(board.Id, session.IssueNumber.Value, session.FixRounds));
            if (scope.ServiceProvider.GetService<AgentOs.Domain.Files.IScopedFileStore>() is { } store)
            {
                await store.DeleteScopeAsync(tenantId, capture.ScopeKey, ct).ConfigureAwait(false);
            }
        }
        var stopLease = stops.Begin(tenantId, session.Id);
        var completion = Task.Run(() => RecordedAsync(tenantId, session, AgentOs.Modules.Pipeline.Agents.AgentRoles.Acceptance, () => TestChangesAsync(tenantId, session, board, pat, changes, cliProfile, stopLease, capture)),
            CancellationToken.None);
        return new TaskRun(TaskStart.Started, completion);
    }

    /// <summary>
    /// Agentic review: the Reviewer agent reads every proposed change of a task (still unreviewed) against its ticket on
    /// the member's computer, posts the review on each pull request, and marks it Reviewed or ChangesRequested (its
    /// must-fix findings become the change request the Coding agent gets next).
    /// </summary>
    public async Task<TaskStart> ReviewAsync(string tenantId, Guid sessionId, string? cliProfile = null, CancellationToken ct = default) =>
        (await BeginReviewAsync(tenantId, sessionId, cliProfile, ct).ConfigureAwait(false)).Status;

    /// <summary><see cref="ReviewAsync"/>, returning the running work too.</summary>
    public async Task<TaskRun> BeginReviewAsync(string tenantId, Guid sessionId, string? cliProfile = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        using var scope = scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var session = (await sessions.ListForTenantAsync(tenantId, ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == sessionId);
        if (session is null) { return TaskStart.TaskNotFound; }
        var board = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>()
            .GetForTenantAsync(tenantId, session.WorkspaceId, ct).ConfigureAwait(false);
        if (board is null) { return TaskStart.BoardNotFound; }
        var changes = (await sessions.ListReposForTenantAsync(tenantId, session.Id, ct).ConfigureAwait(false))
            .Where(r => r.Status == "Done" && !string.IsNullOrEmpty(r.PrUrl) && !string.IsNullOrEmpty(r.BranchName))
            .ToList();
        if (changes.Count == 0) { return TaskStart.NoCodeProject; }
        var pat = await scope.ServiceProvider.GetRequiredService<IAppConfigStore>()
            .GetForTenantAsync(tenantId, board.CredentialRef, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(pat)) { return TaskStart.NoGitHubAccess; }

        await sessions.UpdateRunResultAsync(tenantId, session.Id, "Running", session.PrUrl, null, ct).ConfigureAwait(false);
        foreach (var c in changes)
        {
            await sessions.UpdateRepoRunResultAsync(tenantId, c.Id, "Reviewing", c.BranchName, c.PrUrl, null, ct).ConfigureAwait(false);
        }
        var stopLease = stops.Begin(tenantId, session.Id);
        var completion = Task.Run(() => RecordedAsync(tenantId, session, AgentOs.Modules.Pipeline.Agents.AgentRoles.Reviewer, () => ReviewChangesAsync(tenantId, session, board, pat, changes, cliProfile, stopLease)),
            CancellationToken.None);
        return new TaskRun(TaskStart.Started, completion);
    }

    private async Task ReviewChangesAsync(string tenantId, RemoteSessionEntity session, WorkspaceEntity board, string pat,
        IReadOnlyList<SessionRepoEntity> changes, string? cliProfile, TaskStopRegistry.Lease stopLease)
    {
        using var _stop = stopLease;
        try
        {
            using var scope = scopes.CreateScope();
            using var _identity = AgentOs.SharedKernel.Identity.AmbientIdentity.Push(tenantId, session.MemberUserId, session.Id);
            var sessRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var reviewer = scope.ServiceProvider.GetRequiredService<IReviewAgent>();
            var prs = scope.ServiceProvider.GetRequiredService<IPrCreationService>();
            var body = await ReadTicketBodyAsync(scope, board, tenantId, session, pat).ConfigureAwait(false) ?? "";
            var ticketFiles = await TicketFilesAsync(scope, tenantId, session).ConfigureAwait(false);
            foreach (var change in changes)
            {
                var repoLabel = $"{change.Owner}/{change.Repo}";
                var verdict = await reviewer.RunAsync(new AcceptanceRequest(session.Id, tenantId, session.MemberUserId,
                    new WorkRepo(change.Id, change.Owner, change.Repo, change.DefaultBranch, change.BranchName),
                    session.IssueNumber ?? 0, session.Title, body, cliProfile) { Files = ticketFiles }, stopLease.Token).ConfigureAwait(false);
                if (!verdict.Ran)
                {
                    await sessRepo.UpdateRepoRunResultAsync(tenantId, change.Id, "Done", change.BranchName, change.PrUrl, null).ConfigureAwait(false);
                    feed.Publish(new SessionRunEvent(tenantId, session.Id, SessionRunEventKind.Step, $"{repoLabel} — couldn't review: {verdict.Error}", DateTimeOffset.UtcNow, repoLabel));
                    continue;
                }
                if (PrNumber(change.PrUrl) is { } number)
                {
                    await prs.CommentAsync(RepoDescriptor(board, tenantId, change.Owner, change.Repo, change.DefaultBranch, pat), number,
                        AgentOs.Modules.Pipeline.Agents.ReviewAgent.Comment(session.IssueNumber ?? 0, verdict)).ConfigureAwait(false);
                }
                await sessRepo.UpdateRepoRunResultAsync(tenantId, change.Id, verdict.Approved ? "Reviewed" : "ChangesRequested", change.BranchName, change.PrUrl,
                    verdict.Approved ? null : AgentOs.Modules.Pipeline.Agents.ReviewAgent.ChangeRequest(verdict)).ConfigureAwait(false);
                feed.Publish(new SessionRunEvent(tenantId, session.Id, SessionRunEventKind.RepoDone,
                    verdict.Approved ? $"{repoLabel} — review approved" : $"{repoLabel} — review asked for changes: {verdict.Summary}",
                    DateTimeOffset.UtcNow, repoLabel, verdict.Approved ? "Reviewed" : "ChangesRequested", change.PrUrl));
            }
            await sessRepo.RecomputeSessionStatusForTenantAsync(tenantId, session.Id).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopLease.Token.IsCancellationRequested)
        {
            await MarkStoppedAsync(tenantId, session.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or System.IO.IOException or TimeoutException)
        {
            await MarkFailedAsync(tenantId, session.Id, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The Design gate (ADR-0016): the Designer agent checks the screenshots the Testing agent sent back for the task's
    /// current change against the project's design skill and the ticket, posts its verdict on each pull request, and
    /// records it (pass / changes / needs a person) — the owner can still override it on Board › Tasks.
    /// </summary>
    /// <param name="automaticRetry">Autopilot re-runs a check that couldn't run: counted against
    /// <see cref="DesignReview.MaxAutomaticRetries"/> (a person's "Check again" isn't).</param>
    public async Task<TaskStart> DesignAsync(string tenantId, Guid sessionId, string? cliProfile = null, bool automaticRetry = false, CancellationToken ct = default) =>
        (await BeginDesignAsync(tenantId, sessionId, cliProfile, automaticRetry, ct).ConfigureAwait(false)).Status;

    /// <summary><see cref="DesignAsync"/>, returning the running work too.</summary>
    public async Task<TaskRun> BeginDesignAsync(string tenantId, Guid sessionId, string? cliProfile = null, bool automaticRetry = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        using var scope = scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var session = (await sessions.ListForTenantAsync(tenantId, ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == sessionId);
        if (session is null || session.IssueNumber is not > 0) { return TaskStart.TaskNotFound; }
        var board = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>()
            .GetForTenantAsync(tenantId, session.WorkspaceId, ct).ConfigureAwait(false);
        if (board is null) { return TaskStart.BoardNotFound; }
        var changes = (await sessions.ListReposForTenantAsync(tenantId, session.Id, ct).ConfigureAwait(false))
            .Where(r => AgentOs.Web.Services.Orchestration.ProjectOrchestrator.HoldsAProposedChange(r.Status) && !string.IsNullOrEmpty(r.PrUrl) && !string.IsNullOrEmpty(r.BranchName))
            .ToList();
        if (changes.Count == 0) { return TaskStart.NoCodeProject; }
        var pat = await scope.ServiceProvider.GetRequiredService<IAppConfigStore>()
            .GetForTenantAsync(tenantId, board.CredentialRef, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(pat)) { return TaskStart.NoGitHubAccess; }
        var reviews = scope.ServiceProvider.GetRequiredService<IDesignReviewStore>();
        // Narrow writes only (never the whole row): the owner may decide while the check runs, and that must stand.
        var review = await reviews.EnsureAsync(
            DesignReview.New(tenantId, board.Id, session.Id, session.IssueNumber.Value, session.FixRounds, DesignReviewStatus.Pending), ct).ConfigureAwait(false);
        review = await reviews.SaveVerdictAsync(tenantId, session.Id, review.Run,
            new DesignVerdictUpdate(DesignReviewStatus.Checking, Error: null, CountRetry: automaticRetry && review.Status == DesignReviewStatus.NotRun), ct)
            .ConfigureAwait(false) ?? review;

        var stopLease = stops.Begin(tenantId, session.Id);
        var completion = Task.Run(() => RecordedAsync(tenantId, session, AgentOs.Modules.Pipeline.Agents.AgentRoles.Designer,
            () => DesignChangesAsync(tenantId, session, board, pat, changes, review, cliProfile, stopLease)), CancellationToken.None);
        return new TaskRun(TaskStart.Started, completion);
    }

    private async Task DesignChangesAsync(string tenantId, RemoteSessionEntity session, WorkspaceEntity board, string pat,
        List<SessionRepoEntity> changes, DesignReview review, string? cliProfile, TaskStopRegistry.Lease stopLease)
    {
        using var _stop = stopLease;
        var primary = changes[0];
        var repoLabel = $"{primary.Owner}/{primary.Repo}";
        try
        {
            using var scope = scopes.CreateScope();
            using var _identity = AgentOs.SharedKernel.Identity.AmbientIdentity.Push(tenantId, session.MemberUserId, session.Id);
            var reviews = scope.ServiceProvider.GetRequiredService<IDesignReviewStore>();
            var designer = scope.ServiceProvider.GetRequiredService<IDesignAgent>();
            var files = scope.ServiceProvider.GetRequiredService<AgentOs.Domain.Files.IScopedFileStore>();
            var stored = await files.LoadAllAsync(tenantId, review.ScopeKey, stopLease.Token).ConfigureAwait(false);
            var described = review.Shots.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
            var screenshots = stored
                .Where(f => AgentOs.Domain.Files.RunUploadRules.ContentType(f.Info.Path) is not null)
                .Select(f => new DesignScreenshot(described.TryGetValue(f.Info.Path, out var shot) ? shot : DesignShot.FromPath(f.Info.Path), f.Content, f.Info.ContentType))
                .OrderBy(s => s.Shot.Screen, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Shot.IsPhone ? 0 : 1).ThenBy(s => s.Shot.IsDark ? 1 : 0)
                .ToList();
            var body = await ReadTicketBodyAsync(scope, board, tenantId, session, pat).ConfigureAwait(false) ?? "";
            var skills = await ReadDesignSkillsAsync(scope, board, tenantId, primary, pat).ConfigureAwait(false);
            var verdict = await designer.RunAsync(new DesignRequest(session.Id, tenantId, session.MemberUserId,
                new WorkRepo(primary.Id, primary.Owner, primary.Repo, primary.DefaultBranch, primary.BranchName),
                session.IssueNumber ?? 0, session.Title, body, screenshots, cliProfile) { DesignSkills = skills }, stopLease.Token).ConfigureAwait(false);

            var status = !verdict.Ran ? DesignReviewStatus.NotRun : verdict.Outcome switch
            {
                DesignOutcome.Pass => DesignReviewStatus.Pass,
                DesignOutcome.Changes => DesignReviewStatus.Changes,
                _ => DesignReviewStatus.NeedsPerson,
            };
            // The agent's side only: a decision the owner made while the check ran (Ask for changes / Approve) stands.
            await reviews.SaveVerdictAsync(tenantId, review.SessionId, review.Run, new DesignVerdictUpdate(status, verdict.Summary, verdict.Findings,
                screenshots.Select(s => s.Shot).ToList(), verdict.Error)).ConfigureAwait(false);
            if (verdict.Ran)
            {
                var prs = scope.ServiceProvider.GetRequiredService<IPrCreationService>();
                foreach (var change in changes)
                {
                    if (PrNumber(change.PrUrl) is not { } number) { continue; }
                    await prs.CommentAsync(RepoDescriptor(board, tenantId, change.Owner, change.Repo, change.DefaultBranch, pat), number,
                        AgentOs.Modules.Pipeline.Agents.DesignAgent.Comment(session.IssueNumber ?? 0, verdict, screenshots.Count)).ConfigureAwait(false);
                }
            }
            feed.Publish(new SessionRunEvent(tenantId, session.Id, SessionRunEventKind.Step, status switch
            {
                DesignReviewStatus.Pass => $"{repoLabel} — design check passed",
                DesignReviewStatus.Changes => $"{repoLabel} — design check asked for changes: {verdict.Summary}",
                DesignReviewStatus.NeedsPerson => $"{repoLabel} — the design needs your eye: {verdict.Summary}",
                _ => $"{repoLabel} — couldn't check the design: {verdict.Error}",
            }, DateTimeOffset.UtcNow, repoLabel));
        }
        catch (OperationCanceledException) when (stopLease.Token.IsCancellationRequested)
        {
            await ResetDesignAsync(review, new DesignVerdictUpdate(DesignReviewStatus.Pending)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or System.IO.IOException or TimeoutException
                                   or System.Data.Common.DbException)
        {
            await ResetDesignAsync(review, new DesignVerdictUpdate(DesignReviewStatus.NotRun, Error: ex.Message)).ConfigureAwait(false);
            feed.Publish(new SessionRunEvent(tenantId, session.Id, SessionRunEventKind.Step, $"{repoLabel} — couldn't check the design: {ex.Message}",
                DateTimeOffset.UtcNow, repoLabel));
        }
    }

    // A stopped or failed design check leaves no "Checking" behind: back to waiting (Stop) or to "couldn't run" (an error,
    // which Autopilot re-runs a bounded number of times). The agent's side only — never the owner's decision.
    private async Task ResetDesignAsync(DesignReview review, DesignVerdictUpdate update)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDesignReviewStore>().SaveVerdictAsync(review.TenantId, review.SessionId, review.Run, update)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            _ = ex; // best-effort: the orchestrator's orphan recovery resets it on its next pass
        }
    }

    /// <summary>
    /// The owner overrides the Design gate for the task's current change: <see cref="DesignDecision.Approved"/> lets
    /// Autopilot merge it, <see cref="DesignDecision.ChangesAsked"/> sends <paramref name="note"/> back to the Coding agent
    /// (a fix round). Returns the stored review, or null when the task isn't found.
    /// </summary>
    public async Task<DesignReview?> DecideDesignAsync(string tenantId, Guid sessionId, DesignDecision decision, string? note, string decidedBy,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (decision == DesignDecision.ChangesAsked && string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("Say what should change.", nameof(note));
        }
        using var scope = scopes.CreateScope();
        var session = (await scope.ServiceProvider.GetRequiredService<ISessionRepository>().ListForTenantAsync(tenantId, ct).ConfigureAwait(false))
            .FirstOrDefault(s => s.Id == sessionId);
        if (session is null || session.IssueNumber is not > 0) { return null; }
        var reviews = scope.ServiceProvider.GetRequiredService<IDesignReviewStore>();
        var review = await reviews.EnsureAsync(
            DesignReview.New(tenantId, session.WorkspaceId, session.Id, session.IssueNumber.Value, session.FixRounds, DesignReviewStatus.Pending), ct).ConfigureAwait(false);
        return await DecideAsync(reviews, review, decision, note, decidedBy, ct).ConfigureAwait(false);
    }

    /// <summary>The owner's decision on <paramref name="review"/> (also one shown without its task) — written by the
    /// decision-only update, so a page that shows an older copy of the review never overwrites the agent's verdict.</summary>
    public static async Task<DesignReview?> DecideAsync(IDesignReviewStore reviews, DesignReview review, DesignDecision decision, string? note, string decidedBy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(review);
        if (decision == DesignDecision.ChangesAsked && string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("Say what should change.", nameof(note));
        }
        return await reviews.DecideAsync(review.TenantId, review.SessionId, review.Run,
            new DesignOwnerDecision(decision, note, decidedBy, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
    }

    // The repository's design skill(s) from its default branch. Best-effort: none on any failure.
    private static async Task<IReadOnlyList<RepoInstructionFile>> ReadDesignSkillsAsync(
        IServiceScope scope, WorkspaceEntity board, string tenantId, SessionRepoEntity primary, string pat)
    {
        var resolver = scope.ServiceProvider.GetRequiredService<ISourceProviderResolver>();
        if (!resolver.TryResolve(board.Kind, out var provider) || provider is null) { return []; }
        try
        {
            return await provider.ReadDesignSkillsAsync(RepoDescriptor(board, tenantId, primary.Owner, primary.Repo, primary.DefaultBranch, pat)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Octokit.ApiException or InvalidOperationException or TaskCanceledException)
        {
            return [];
        }
    }

    private async Task TestChangesAsync(string tenantId, RemoteSessionEntity session, WorkspaceEntity board, string pat,
        List<SessionRepoEntity> changes, string? cliProfile, TaskStopRegistry.Lease stopLease, DesignCapture? capture = null)
    {
        using var _stop = stopLease;
        try
        {
            using var scope = scopes.CreateScope();
            using var _identity = AgentOs.SharedKernel.Identity.AmbientIdentity.Push(tenantId, session.MemberUserId, session.Id);
            var sessRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var tester = scope.ServiceProvider.GetRequiredService<IAcceptanceAgent>();
            // Acceptance runs on one computer are capped, whoever asks (a MAF ticket workflow or a step-by-step pass): each
            // brings the app up on the computer's fixed ports. The cap is the tenant's setting, read now.
            var settings = scope.ServiceProvider.GetService<AgentOs.Domain.Configuration.ISettings>();
            var testCap = settings is null ? AgentOs.Modules.Pipeline.Agents.AutopilotLimits.Default.MaxParallelTests
                : (await AgentOs.Modules.Pipeline.Agents.AutopilotSettings.ReadLimitsAsync(settings, tenantId, stopLease.Token).ConfigureAwait(false)).MaxParallelTests;
            using var _slot = await _testGate.EnterAsync(tenantId + "/" + session.MemberUserId, testCap, stopLease.Token).ConfigureAwait(false);
            var prs = scope.ServiceProvider.GetRequiredService<IPrCreationService>();
            var body = await ReadTicketBodyAsync(scope, board, tenantId, session, pat).ConfigureAwait(false) ?? "";
            var ticketFiles = await TicketFilesAsync(scope, tenantId, session).ConfigureAwait(false);
            var shots = new List<DesignShot>();
            var allPassed = changes.Count > 0;
            foreach (var change in changes)
            {
                var repoLabel = $"{change.Owner}/{change.Repo}";
                var verdict = await tester.RunAsync(new AcceptanceRequest(session.Id, tenantId, session.MemberUserId,
                    new WorkRepo(change.Id, change.Owner, change.Repo, change.DefaultBranch, change.BranchName),
                    session.IssueNumber ?? 0, session.Title, body, cliProfile)
                {
                    Files = ticketFiles,
                    // Each repo's screenshots under its own folder of the task's design scope — two repos never overwrite each other.
                    Design = capture is null ? null : capture with { PathPrefix = DesignCapture.PrefixFor(change.Id) },
                }, stopLease.Token).ConfigureAwait(false);
                allPassed &= verdict is { Ran: true, Passed: true };
                shots.AddRange(verdict.Screenshots);
                if (!verdict.Ran)
                {
                    // Couldn't test (no computer, timed out): the change stays as it was, the reason is on the feed.
                    await sessRepo.UpdateRepoRunResultAsync(tenantId, change.Id, "Done", change.BranchName, change.PrUrl, null).ConfigureAwait(false);
                    feed.Publish(new SessionRunEvent(tenantId, session.Id, SessionRunEventKind.Step, $"{repoLabel} — couldn't test: {verdict.Error}", DateTimeOffset.UtcNow, repoLabel));
                    continue;
                }
                if (PrNumber(change.PrUrl) is { } number)
                {
                    await prs.CommentAsync(RepoDescriptor(board, tenantId, change.Owner, change.Repo, change.DefaultBranch, pat), number,
                        AgentOs.Modules.Pipeline.Agents.AcceptanceAgent.Comment(session.IssueNumber ?? 0, verdict)).ConfigureAwait(false);
                }
                if (verdict.BaseFailures.Count > 0)
                {
                    await OpenBaseFixTicketsAsync(scope, board, tenantId, change, pat, verdict.BaseFailures).ConfigureAwait(false);
                }
                await sessRepo.UpdateRepoRunResultAsync(tenantId, change.Id, verdict.Passed ? "Accepted" : "Rejected", change.BranchName, change.PrUrl,
                    verdict.Passed ? null : AgentOs.Modules.Pipeline.Agents.AcceptanceAgent.Findings(verdict)).ConfigureAwait(false);
                feed.Publish(new SessionRunEvent(tenantId, session.Id, SessionRunEventKind.RepoDone,
                    verdict.Passed ? $"{repoLabel} — passed testing" : $"{repoLabel} — failed testing: {verdict.Summary}",
                    DateTimeOffset.UtcNow, repoLabel, verdict.Passed ? "Accepted" : "Rejected", change.PrUrl));
            }
            // Design gate: a passed change waits for the Designer with the screenshots it sent back (none is a person's call).
            if (capture is not null && allPassed && session.IssueNumber is > 0
                && scope.ServiceProvider.GetService<IDesignReviewStore>() is { } reviews)
            {
                // Same fix round = same code: a re-test refreshes the screenshots and waits for the Designer again, but an
                // owner's decision on this round stands (narrow writes only).
                var fresh = shots.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();
                await reviews.EnsureAsync(DesignReview.New(tenantId, board.Id, session.Id, session.IssueNumber.Value, session.FixRounds, DesignReviewStatus.Pending))
                    .ConfigureAwait(false);
                await reviews.SaveVerdictAsync(tenantId, session.Id, session.FixRounds,
                    new DesignVerdictUpdate(DesignReviewStatus.Pending, string.Empty, [], fresh)).ConfigureAwait(false);
                feed.Publish(new SessionRunEvent(tenantId, session.Id, SessionRunEventKind.Step,
                    shots.Count > 0 ? $"{shots.Count} screenshot(s) ready for the design check." : "No screenshots came back — the design check will need you.",
                    DateTimeOffset.UtcNow));
            }
            await sessRepo.RecomputeSessionStatusForTenantAsync(tenantId, session.Id).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopLease.Token.IsCancellationRequested)
        {
            await MarkStoppedAsync(tenantId, session.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or System.IO.IOException or TimeoutException)
        {
            await MarkFailedAsync(tenantId, session.Id, ex.Message).ConfigureAwait(false);
        }
    }

    // A test the Testing agent found failing on the base branch too gets one bug ticket (p0, approved for the AI), so
    // Autopilot fixes the base instead of bouncing every change that happens to run it. Deduplicated by title against
    // the board's open tickets. Best-effort.
    private async Task OpenBaseFixTicketsAsync(IServiceScope scope, WorkspaceEntity board, string tenantId, SessionRepoEntity change,
        string pat, IReadOnlyList<AcceptanceCriterion> failures)
    {
        try
        {
            var boardDesc = new BoardDescriptor(board.Id, tenantId, board.Kind, board.ProjectOwner, board.ProjectScope,
                board.ProjectNumber, board.ProjectNodeId, pat, board.Project);
            var open = (await scope.ServiceProvider.GetRequiredService<IBoardTicketService>().ListTicketsAsync(boardDesc).ConfigureAwait(false)).Items
                .Where(t => !string.Equals(t.State, "CLOSED", StringComparison.OrdinalIgnoreCase)).Select(t => t.Title).ToHashSet(StringComparer.Ordinal);
            var drafts = failures
                .Select(f => new TicketDraft(
                    $"Fix the failing test on {change.DefaultBranch}: {ShortTestName(f.Criterion)}",
                    $"The AgentOS.NET Testing agent found `{f.Criterion}` failing on `{change.DefaultBranch}` while testing {change.PrUrl} "
                    + $"— the failure is not caused by that change.\n\n{f.Evidence}\n\n## Acceptance criteria\n- [ ] `{f.Criterion}` passes on {change.DefaultBranch}.\n"
                    + "- [ ] The rest of the test suite still passes.",
                    ["type:bug", "p0", StandardLabels.AiReady], true))
                .Where(d => !open.Contains(d.Title))
                .DistinctBy(d => d.Title)
                .ToList();
            if (drafts.Count == 0) { return; }
            var repo = RepoDescriptor(board, tenantId, change.Owner, change.Repo, change.DefaultBranch, pat);
            var created = await scope.ServiceProvider.GetRequiredService<IBoardWriteService>().CreateTicketsAsync(boardDesc, repo, drafts).ConfigureAwait(false);
            foreach (var t in created)
            {
                feed.Publish(new SessionRunEvent(tenantId, Guid.Empty, SessionRunEventKind.Step,
                    $"Opened #{t.Number}: {t.Title}", DateTimeOffset.UtcNow, $"{change.Owner}/{change.Repo}"));
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Octokit.ApiException or InvalidOperationException or TimeoutException)
        {
            _ = ex; // best-effort: the comment on the pull request still lists the failure
        }
    }

    private static string ShortTestName(string test)
    {
        var name = test.Trim();
        var dot = name.LastIndexOf('.', name.Length - 1);
        var cls = dot > 0 ? name[..dot] : name;
        var lastClass = cls.LastIndexOf('.');
        var shortName = dot > 0 ? (lastClass >= 0 ? cls[(lastClass + 1)..] : cls) + name[dot..] : name;
        return shortName.Length > 90 ? shortName[..90] + "…" : shortName;
    }

    // A fresh attempt must not reuse a branch an earlier, interrupted attempt left on the remote: its push would be
    // rejected as non-fast-forward and the agent may not force-push (#15 on the lab finished its work and could not push
    // it). The first free of issue-N-ai-fix, -2 … -9; null (the default name) when it can't be checked.
    internal static async Task<string?> FreeBranchAsync(IPrCreationService prs, WorkspaceDescriptor repo, int issueNumber)
    {
        ArgumentNullException.ThrowIfNull(prs);
        if (issueNumber <= 0) { return null; }
        var name = WorkRepo.DefaultBranchName(issueNumber);
        for (var attempt = 1; attempt <= 9; attempt++)
        {
            var candidate = attempt == 1 ? name : $"{name}-{attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var exists = await prs.BranchExistsAsync(repo, candidate).ConfigureAwait(false);
            if (exists is null) { return null; }
            if (exists == false) { return attempt == 1 ? null : candidate; }
        }
        return $"{name}-{DateTimeOffset.UtcNow:yyyyMMddHHmm}";
    }

    /// <summary>
    /// Metrics first: run one agent step of a task inside an LLM call scope, then store it as a run with its calls
    /// (tokens, latency, provider, outcome) — Insights reads those. Best-effort: metrics never fail the task.
    /// </summary>
    internal async Task RecordedAsync(string tenantId, RemoteSessionEntity session, string agent, Func<Task> run)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(run);
        var started = DateTimeOffset.UtcNow;
        using var calls = AgentOs.Domain.Llm.LlmCallScope.Begin();
        try
        {
            await run().ConfigureAwait(false);
        }
        finally
        {
            await SaveAgentRunAsync(tenantId, session.MemberUserId, agent,
                    $"{AgentOs.Modules.Pipeline.Agents.AgentRoles.Label(agent)} · #{session.IssueNumber} {session.Title}", started, calls.Calls)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Store an agent run and its LLM calls (nothing when it made none). Never throws.</summary>
    public async Task SaveAgentRunAsync(string tenantId, string memberId, string agent, string label, DateTimeOffset started,
        IReadOnlyList<AgentOs.Domain.Llm.LlmCallRecord> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count == 0) { return; }
        try
        {
            using var _identity = AgentOs.SharedKernel.Identity.AmbientIdentity.Push(tenantId, memberId);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AgentOs.Modules.Pipeline.Persistence.IPipelineRunRepository>()
                .SaveAgentRunAsync(new AgentOs.Modules.Pipeline.Persistence.AgentRunRecord(Guid.NewGuid(),
                    label.Length > 200 ? label[..200] : label, agent, calls.All(c => c.Success), started, DateTimeOffset.UtcNow, calls), tenantId)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex; // metrics are best-effort: a lost row never fails the task
        }
    }

    /// <summary>The number of a pull request from its URL (…/pull/42).</summary>
    // A follow-up (Ask for changes / a fix round) that failed keeps the change it was fixing when there is one: an open
    // pull request in a proposed-change state, or one a person was asked about (Blocked).
    internal static bool FailedFollowUpKeepsItsChange(bool isFollowUp, string? status, string? prUrl) =>
        isFollowUp && !string.IsNullOrEmpty(prUrl)
        && (status == "Blocked" || AgentOs.Web.Services.Orchestration.ProjectOrchestrator.HoldsAProposedChange(status));

    public static int? PrNumber(string? url)
    {
        if (string.IsNullOrEmpty(url)) { return null; }
        var i = url.LastIndexOf("/pull/", StringComparison.Ordinal);
        return i >= 0 && int.TryParse(url.AsSpan(i + "/pull/".Length).TrimEnd('/'), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private async Task RunAsync(string tenantId, RemoteSessionEntity session, WorkspaceEntity board, string pat,
        IReadOnlyList<SessionRepoEntity> children, string? cliProfile, string? change, TaskStopRegistry.Lease stopLease)
    {
        using var _stop = stopLease;
        void Publish(SessionRunEventKind kind, string message, string? repo = null, string? status = null, string? prUrl = null) =>
            feed.Publish(new SessionRunEvent(tenantId, session.Id, kind, message, DateTimeOffset.UtcNow, repo, status, prUrl));
        try
        {
            using var scope = scopes.CreateScope();
            // No HttpContext here, so seed the tenant + member as the ambient identity — the LLM tool tenant and the
            // runner dispatch read it to target the right runner, and runner_shell tags its progress to this session.
            using var _identity = AgentOs.SharedKernel.Identity.AmbientIdentity.Push(tenantId, session.MemberUserId, session.Id);
            var sessRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

            // Thorough engine — the 5-agent SDLC pipeline, server-side, greenfield only (hard-gated by the router).
            if (EngineSelection.IsQuality(session.Brain))
            {
                await RunQualityAsync(scope, sessRepo, session, board, pat, children, tenantId, stopLease.Token).ConfigureAwait(false);
                return;
            }

            var agent = scope.ServiceProvider.GetRequiredService<IIssueWorkAgent>();
            var prService = scope.ServiceProvider.GetRequiredService<IPrCreationService>();
            var issueBody = await ReadTicketBodyAsync(scope, board, tenantId, session, pat).ConfigureAwait(false);
            var ticketFiles = await TicketFilesAsync(scope, tenantId, session).ConfigureAwait(false);
            if (ticketFiles.Count > 0)
            {
                Publish(SessionRunEventKind.Step, $"Sending the {ticketFiles.Count} file(s) attached to this ticket with the run — they are copied into the agent's working folder.");
            }

            // Ask for changes: each repo that already has a pull request continues on its branch.
            var workRepos = new List<WorkRepo>(children.Count);
            foreach (var c in children)
            {
                var continuing = change is not null && !string.IsNullOrEmpty(c.PrUrl);
                workRepos.Add(new WorkRepo(c.Id, c.Owner, c.Repo, c.DefaultBranch, continuing ? c.BranchName : null)
                {
                    NewBranch = continuing ? null
                        : await FreeBranchAsync(prService, RepoDescriptor(board, tenantId, c.Owner, c.Repo, c.DefaultBranch, pat),
                            session.IssueNumber ?? 0).ConfigureAwait(false),
                });
            }
            var issueRequest = new IssueWorkRequest(
                SessionId: session.Id,
                TenantId: tenantId,
                MemberId: session.MemberUserId,
                Repos: workRepos,
                IssueNumber: session.IssueNumber ?? 0,
                IssueTitle: session.Title,
                IssueBody: issueBody ?? "",
                TicketKind: session.TicketKind ?? "Issue",
                ProviderOverride: session.RunOnMachine ? "RemoteAgent" : null,
                CliProfile: session.RunOnMachine ? cliProfile : null,
                TicketType: session.TicketType,
                ChangeRequest: change)
            {
                Files = ticketFiles,
            };

            var workResult = await agent.RunAsync(issueRequest, stopLease.Token).ConfigureAwait(false);

            // One PR per repo that pushed a branch; each repo's result is written back on its own.
            var anyFailure = false;
            foreach (var outcome in workResult.Repos)
            {
                var repoLabel = $"{outcome.Owner}/{outcome.Repo}";
                if (!outcome.Ok && AgentOs.Modules.RemoteAgent.PlanLimitDetector.IsPlanLimit(outcome.Error))
                {
                    // Nothing failed — the member's AI plan said "not now". A follow-up's change goes back to where it
                    // stood (its checks / findings intact), so the same move is made after the reset; a fresh start
                    // waits as cut short, and Autopilot picks it up again once the plan resets.
                    var before = children.FirstOrDefault(c => c.Id == outcome.SessionRepoId);
                    if (change is not null && before is { PrUrl.Length: > 0, Status: "Done" or "Reviewed" or "ChangesRequested" or "Accepted" or "Rejected" or "Blocked" })
                    {
                        await sessRepo.UpdateRepoRunResultAsync(tenantId, before.Id, before.Status, before.BranchName, before.PrUrl, before.Error).ConfigureAwait(false);
                    }
                    else
                    {
                        anyFailure = true;
                        await sessRepo.UpdateRepoRunResultAsync(tenantId, outcome.SessionRepoId, "Failed", outcome.BranchName,
                            change is not null ? before?.PrUrl : null, outcome.Error).ConfigureAwait(false);
                    }
                    Publish(SessionRunEventKind.RepoDone, $"{repoLabel} — paused: your computer's AI plan hit its limit. Nothing was changed.", repoLabel,
                        before?.Status ?? "Failed", before?.PrUrl);
                    continue;
                }
                if (!outcome.Ok)
                {
                    anyFailure = true;
                    // A follow-up that failed leaves the change where it stood — its pull request, branch and status
                    // (with the reason recorded) — so Autopilot sends it back again or asks a person, counting the round.
                    // It used to mark the repo Failed and drop the pull request link: Autopilot lost track of the open PR
                    // and started the ticket over on a new branch (VNChess #95 / PR #97, 2026-10-24).
                    var stood = children.FirstOrDefault(c => c.Id == outcome.SessionRepoId);
                    if (stood is not null && FailedFollowUpKeepsItsChange(change is not null, stood.Status, stood.PrUrl))
                    {
                        await sessRepo.UpdateRepoRunResultAsync(tenantId, stood.Id, stood.Status, stood.BranchName, stood.PrUrl,
                            "The last fix attempt failed: " + (outcome.Error ?? "Agent failed.")).ConfigureAwait(false);
                        Publish(SessionRunEventKind.RepoDone, $"{repoLabel} — the fix attempt failed: {outcome.Error}. The pull request is unchanged.",
                            repoLabel, stood.Status, stood.PrUrl);
                        continue;
                    }
                    await sessRepo.UpdateRepoRunResultAsync(tenantId, outcome.SessionRepoId,
                        "Failed", outcome.BranchName, null, outcome.Error ?? "Agent failed.").ConfigureAwait(false);
                    Publish(SessionRunEventKind.RepoDone, $"{repoLabel} — failed: {outcome.Error}", repoLabel, "Failed");
                    continue;
                }

                // A follow-up pushed more commits to an open pull request: nothing new to open.
                var child = children.FirstOrDefault(c => c.Id == outcome.SessionRepoId);
                if (change is not null && !string.IsNullOrEmpty(child?.PrUrl))
                {
                    // The agent's updated description replaces the old one: a ticket that asks the pull request itself
                    // for something (an old → new package list) could otherwise never pass review — the agent can't edit
                    // the description, so every fix round failed on it (VNChess #94, 2026-10-24).
                    var described = false;
                    if (!string.IsNullOrWhiteSpace(outcome.Description) && PrNumber(child.PrUrl) is { } openNumber)
                    {
                        described = await prService.UpdateBodyAsync(
                            RepoDescriptor(board, tenantId, outcome.Owner, outcome.Repo, child.DefaultBranch ?? "main", pat), openNumber,
                            PullRequestText.Body(session.IssueNumber, outcome.Summary, outcome.Description)).ConfigureAwait(false);
                    }
                    await sessRepo.UpdateRepoRunResultAsync(tenantId, outcome.SessionRepoId,
                        "Done", outcome.BranchName, child.PrUrl, null).ConfigureAwait(false);
                    Publish(SessionRunEventKind.RepoDone,
                        described ? $"{repoLabel} — proposed change and its description updated" : $"{repoLabel} — proposed change updated",
                        repoLabel, "Done", child.PrUrl);
                    continue;
                }

                var descriptor = RepoDescriptor(board, tenantId, outcome.Owner, outcome.Repo, child?.DefaultBranch ?? "main", pat);
                try
                {
                    var prResult = await prService.CreateFromBranchAsync(
                        descriptor,
                        outcome.BranchName,
                        PullRequestText.Title(session.IssueNumber, session.Title, session.TicketType),
                        PullRequestText.Body(session.IssueNumber, outcome.Summary, outcome.Description)).ConfigureAwait(false);

                    await sessRepo.UpdateRepoRunResultAsync(tenantId, outcome.SessionRepoId,
                        prResult.Ok ? "Done" : "Failed", outcome.BranchName, prResult.HtmlUrl,
                        prResult.Ok ? null : prResult.Error).ConfigureAwait(false);
                    if (!prResult.Ok) { anyFailure = true; }
                    else
                    {
                        await AgentOs.Domain.Memory.ProjectMemory.RecordChangeAsync(scope.ServiceProvider.GetRequiredService<AgentOs.Domain.Memory.IMemoryStore>(),
                            tenantId, outcome.Owner, outcome.Repo, session.IssueNumber, session.Title, prResult.HtmlUrl ?? "",
                            PullRequestText.Title(session.IssueNumber, session.Title, session.TicketType), outcome.Summary).ConfigureAwait(false);
                    }
                    Publish(SessionRunEventKind.RepoDone,
                        prResult.Ok ? $"{repoLabel} — PR opened" : $"{repoLabel} — PR failed: {prResult.Error}",
                        repoLabel, prResult.Ok ? "Done" : "Failed", prResult.HtmlUrl);
                }
                catch (Exception prEx) when (prEx is System.Net.Http.HttpRequestException or System.Text.Json.JsonException
                                              or InvalidOperationException or System.IO.IOException or TimeoutException)
                {
                    anyFailure = true;
                    await sessRepo.UpdateRepoRunResultAsync(tenantId, outcome.SessionRepoId,
                        "Failed", outcome.BranchName, null, $"PR creation failed: {prEx.Message}").ConfigureAwait(false);
                    Publish(SessionRunEventKind.RepoDone, $"{repoLabel} — PR creation failed: {prEx.Message}", repoLabel, "Failed");
                }
            }

            // A follow-up the plan limit stopped everywhere never happened: its fix round is taken back, so the cap
            // (Settings › Autopilot) counts only rounds that ran.
            if (change is not null && workResult.Repos.Count > 0
                && workResult.Repos.All(o => !o.Ok && AgentOs.Modules.RemoteAgent.PlanLimitDetector.IsPlanLimit(o.Error)))
            {
                await sessRepo.DecrementFixRoundsForTenantAsync(tenantId, session.Id).ConfigureAwait(false);
            }
            await sessRepo.RecomputeSessionStatusForTenantAsync(tenantId, session.Id).ConfigureAwait(false);
            Publish(anyFailure ? SessionRunEventKind.Failed : SessionRunEventKind.Done,
                anyFailure ? "Session finished with failures." : "Session complete — all repos done.");
        }
        catch (OperationCanceledException) when (stopLease.Token.IsCancellationRequested)
        {
            await MarkStoppedAsync(tenantId, session.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException
                                   or Microsoft.AspNetCore.SignalR.HubException or InvalidOperationException
                                   or System.IO.IOException or TimeoutException)
        {
            await MarkFailedAsync(tenantId, session.Id, ex.Message).ConfigureAwait(false);
        }
    }

    // Stop: the task and every repo that hadn't finished read "Stopped", and Try again is offered.
    private async Task MarkStoppedAsync(string tenantId, Guid sessionId)
    {
        const string stopped = "Stopped — you pressed Stop. Nothing was pushed for the unfinished parts; Try again to restart.";
        try
        {
            using var scope = scopes.CreateScope();
            var sessRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var unfinished = (await sessRepo.ListReposForTenantAsync(tenantId, sessionId).ConfigureAwait(false))
                .Where(child => child.Status is "Pending" or "Running");
            foreach (var child in unfinished)
            {
                await sessRepo.UpdateRepoRunResultAsync(tenantId, child.Id, "Failed", null, null, "Stopped").ConfigureAwait(false);
            }
            await sessRepo.UpdateRunResultAsync(tenantId, sessionId, "Failed", null, stopped).ConfigureAwait(false);
        }
        catch (Exception inner) when (inner is System.Net.Http.HttpRequestException or System.IO.IOException or InvalidOperationException or TimeoutException) { _ = inner.Message; }
        feed.Publish(new SessionRunEvent(tenantId, sessionId, SessionRunEventKind.Failed, "Stopped by you.", DateTimeOffset.UtcNow));
    }

    private async Task MarkFailedAsync(string tenantId, Guid sessionId, string message)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISessionRepository>()
                .UpdateRunResultAsync(tenantId, sessionId, "Failed", null, message).ConfigureAwait(false);
        }
        // Best-effort.
        catch (Exception inner) when (inner is System.Net.Http.HttpRequestException or System.IO.IOException or InvalidOperationException or TimeoutException) { _ = inner.Message; }
        feed.Publish(new SessionRunEvent(tenantId, sessionId, SessionRunEventKind.Failed, $"Session failed: {message}", DateTimeOffset.UtcNow));
    }

    // ── Thorough engine (5-agent pipeline) ─────────────────────────────────────────────────────────────────

    // Runs the 5-agent SDLC pipeline against the PRIMARY repo and opens a PR with the generated code + tests,
    // server-side. Hard-gated: a non-greenfield ticket is REFUSED, never silently downgraded to Fast.
    private async Task RunQualityAsync(IServiceScope scope, ISessionRepository sessRepo, RemoteSessionEntity session,
        WorkspaceEntity board, string pat, IReadOnlyList<SessionRepoEntity> children, string tenantId, CancellationToken stop)
    {
        void Publish(SessionRunEventKind kind, string message, string? repo = null, string? status = null, string? prUrl = null) =>
            feed.Publish(new SessionRunEvent(tenantId, session.Id, kind, message, DateTimeOffset.UtcNow, repo, status, prUrl));

        var cls = TicketClassifier.Classify(session.TicketType, session.Title);
        if (cls != TicketClass.Greenfield)
        {
            var reason = TicketClassifier.RefusalMessage(cls);
            await sessRepo.UpdateRunResultAsync(tenantId, session.Id, "Failed", null, reason, CancellationToken.None).ConfigureAwait(false);
            Publish(SessionRunEventKind.Failed, reason);
            return;
        }

        var primary = children.Count > 0 ? children[0] : null;
        if (primary is null)
        {
            const string noRepo = "Quality run has no target repo — recreate the session from a board ticket.";
            await sessRepo.UpdateRunResultAsync(tenantId, session.Id, "Failed", null, noRepo, CancellationToken.None).ConfigureAwait(false);
            Publish(SessionRunEventKind.Failed, noRepo);
            return;
        }
        var repoLabel = $"{primary.Owner}/{primary.Repo}";

        // Heavy services resolved lazily, inside this background scope.
        var pipeline = scope.ServiceProvider.GetRequiredService<IPipelineClient>();
        var prService = scope.ServiceProvider.GetRequiredService<IGitHubPrService>();

        Publish(SessionRunEventKind.Running, $"Quality engine — 5-agent pipeline on {repoLabel}.", repoLabel);
        if ((await TicketFilesAsync(scope, tenantId, session).ConfigureAwait(false)).Count > 0)
        {
            Publish(SessionRunEventKind.Step, "The files attached to this ticket are used by runs on a computer — the Thorough engine works on our servers without them.", repoLabel);
        }

        var ticketBody = await ReadTicketBodyAsync(scope, board, tenantId, session, pat).ConfigureAwait(false);
        var story = IssueToStoryAdapter.ToUserStory(session.Title, ticketBody, QualityNMax());

        // E0.7: the repo's own agent instructions guide every agent of this run; the feed says which files were used.
        var guidance = await ReadRepoGuidanceAsync(scope, board, tenantId, primary, pat).ConfigureAwait(false);
        Publish(SessionRunEventKind.Step, guidance.Count == 0
            ? $"{repoLabel} — no repo agent instructions found (AGENTS.md, CLAUDE.md, …)."
            : $"{repoLabel} — following repo instructions: {RepoInstructions.Describe(guidance)}.", repoLabel);
        using var guidanceScope = RunGuidance.Push(RepoInstructions.ToPromptBlock(guidance));

        PipelineResult? result = null;
        string? error = null;
        await foreach (var evt in pipeline.StreamAsync(story, stop).ConfigureAwait(false))
        {
            switch (evt.Kind)
            {
                case PipelineStreamEventKind.Progress when evt.Progress is not null:
                    Publish(SessionRunEventKind.Step, PipelineProgressFormatter.Describe(evt.Progress), repoLabel);
                    break;
                case PipelineStreamEventKind.Result:
                    result = evt.Result;
                    break;
                case PipelineStreamEventKind.Error:
                    error = evt.Error ?? "Pipeline error.";
                    break;
            }
        }

        if (result is null || result.Status == PipelineStatus.Failed)
        {
            var msg = error ?? "Quality pipeline failed — see the server log.";
            await sessRepo.UpdateRepoRunResultAsync(tenantId, primary.Id, "Failed", null, null, msg, CancellationToken.None).ConfigureAwait(false);
            await sessRepo.UpdateRunResultAsync(tenantId, session.Id, "Failed", null, msg, CancellationToken.None).ConfigureAwait(false);
            Publish(SessionRunEventKind.Failed, $"{repoLabel} — {msg}", repoLabel, "Failed");
            return;
        }

        // QA gate: only a result QA PASSED on real model output becomes a PR; anything else is Blocked with the reason.
        var gate = PrGate.Evaluate(result);
        if (!gate.Allowed)
        {
            await sessRepo.UpdateRepoRunResultAsync(tenantId, primary.Id, "Blocked", null, null, gate.Reason, CancellationToken.None).ConfigureAwait(false);
            await sessRepo.RecomputeSessionStatusForTenantAsync(tenantId, session.Id, CancellationToken.None).ConfigureAwait(false);
            Publish(SessionRunEventKind.Failed, $"{repoLabel} — {gate.Reason}", repoLabel, "Blocked");
            return;
        }

        var descriptor = RepoDescriptor(board, tenantId, primary.Owner, primary.Repo, primary.DefaultBranch, pat);
        var title = PullRequestText.Title(session.IssueNumber, session.Title, session.TicketType);
        var body = PullRequestText.Body(session.IssueNumber,
            $"Generated by the AgentOS.NET Thorough pipeline (5-agent SDLC loop) for ticket #{session.IssueNumber}.");
        try
        {
            var pr = await prService.OpenPrAsync(result, descriptor, title, body, CancellationToken.None).ConfigureAwait(false);
            await sessRepo.UpdateRepoRunResultAsync(tenantId, primary.Id, "Done", pr.Branch, pr.HtmlUrl, null, CancellationToken.None).ConfigureAwait(false);
            await sessRepo.RecomputeSessionStatusForTenantAsync(tenantId, session.Id, CancellationToken.None).ConfigureAwait(false);
            Publish(SessionRunEventKind.Done, $"{repoLabel} — PR opened.", repoLabel, "Done", pr.HtmlUrl);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException
                                   or InvalidOperationException or ArgumentException or System.IO.IOException or TimeoutException)
        {
            await sessRepo.UpdateRepoRunResultAsync(tenantId, primary.Id, "Failed", null, null, $"PR creation failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            await sessRepo.UpdateRunResultAsync(tenantId, session.Id, "Failed", null, $"PR creation failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            Publish(SessionRunEventKind.Failed, $"{repoLabel} — PR creation failed: {ex.Message}", repoLabel, "Failed");
        }
    }

    /// <summary>The QA-loop iteration cap for a Thorough run, clamped to the admin bound (never above Pipeline:MaxIterations).</summary>
    public int QualityNMax() => Math.Clamp(Math.Min(3, pipelineOptions.Value.MaxIterations), 1, 10);

    /// <summary>A repo of a board as the source-control services address it.</summary>
    public static WorkspaceDescriptor RepoDescriptor(
        WorkspaceEntity board, string tenantId, string owner, string repo, string defaultBranch, string pat)
    {
        ArgumentNullException.ThrowIfNull(board);
        return new(board.Id, tenantId, board.Kind, owner, repo, board.Project,
            string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch, pat);
    }

    // The ticket's CURRENT body (third-party text — the agent prompts fence it as untrusted data). Best-effort: null on
    // any failure, and the run proceeds on the title alone.
    private static async Task<string?> ReadTicketBodyAsync(
        IServiceScope scope, WorkspaceEntity board, string tenantId, RemoteSessionEntity session, string pat)
    {
        if (session.IssueNumber is not > 0 || string.IsNullOrWhiteSpace(session.RepoOwner) || string.IsNullOrWhiteSpace(session.RepoName))
        {
            return null;
        }
        var resolver = scope.ServiceProvider.GetRequiredService<ISourceProviderResolver>();
        if (!resolver.TryResolve(board.Kind, out var provider) || provider is null)
        {
            return null;
        }
        var repo = RepoDescriptor(board, tenantId, session.RepoOwner, session.RepoName, session.RepoDefaultBranch ?? "main", pat);
        try
        {
            return await provider.ReadTicketBodyAsync(repo, session.IssueNumber.Value).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Octokit.ApiException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
    }

    // Ticket files (ADR-0015): what a person attached to this task's ticket, sent with every run of it. Best-effort: a
    // store failure is said on the feed and the run goes on without them.
    private async Task<IReadOnlyList<AgentOs.Domain.Files.RunFile>> TicketFilesAsync(IServiceScope scope, string tenantId, RemoteSessionEntity session)
    {
        if (scope.ServiceProvider.GetService<AgentOs.Domain.Files.ITicketFiles>() is not { } files) { return []; }
        try
        {
            return await files.ForRunAsync(tenantId,
                AgentOs.Domain.Files.TicketFileScope.ForTask(session.WorkspaceId, session.IssueNumber, session.Id)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or TimeoutException or System.Data.Common.DbException)
        {
            feed.Publish(new SessionRunEvent(tenantId, session.Id, SessionRunEventKind.Step,
                $"Couldn't read the files attached to this ticket ({ex.Message}) — the run goes on without them.", DateTimeOffset.UtcNow));
            return [];
        }
    }

    // The target repo's agent-instruction files, budgeted (E0.7). Best-effort: no guidance on failure.
    private static async Task<IReadOnlyList<RepoInstructionFile>> ReadRepoGuidanceAsync(
        IServiceScope scope, WorkspaceEntity board, string tenantId, SessionRepoEntity primary, string pat)
    {
        var resolver = scope.ServiceProvider.GetRequiredService<ISourceProviderResolver>();
        if (!resolver.TryResolve(board.Kind, out var provider) || provider is null)
        {
            return [];
        }
        var repo = RepoDescriptor(board, tenantId, primary.Owner, primary.Repo, primary.DefaultBranch, pat);
        try
        {
            return (await provider.ReadRepoContextAsync(repo).ConfigureAwait(false)).Instructions;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Octokit.ApiException or InvalidOperationException or TaskCanceledException)
        {
            return [];
        }
    }
}
