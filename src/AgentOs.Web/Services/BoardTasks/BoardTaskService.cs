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

/// <summary>Starts Board tasks and follow-ups. Singleton; every call is tenant-explicit.</summary>
public sealed class BoardTaskService(
    IServiceScopeFactory scopes,
    ISessionRunFeed feed,
    TaskStopRegistry stops,
    IOptions<PipelineOptions> pipelineOptions)
{
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
        _ = Task.Run(() => RunAsync(tenantId, session, board, pat, children, cliProfile, change, stopLease), CancellationToken.None);
        return TaskStart.Started;
    }

    /// <summary>
    /// Agentic Testing: test every open proposed change of a task against its ticket's acceptance criteria on the
    /// member's computer, post the evidence on each pull request, and mark it Accepted or Rejected.
    /// </summary>
    public async Task<TaskStart> TestAsync(string tenantId, Guid sessionId, string? cliProfile = null, CancellationToken ct = default)
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
            .Where(r => r.Status is "Done" or "Accepted" or "Rejected" && !string.IsNullOrEmpty(r.PrUrl) && !string.IsNullOrEmpty(r.BranchName))
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
        var stopLease = stops.Begin(tenantId, session.Id);
        _ = Task.Run(() => TestChangesAsync(tenantId, session, board, pat, changes, cliProfile, stopLease), CancellationToken.None);
        return TaskStart.Started;
    }

    private async Task TestChangesAsync(string tenantId, RemoteSessionEntity session, WorkspaceEntity board, string pat,
        IReadOnlyList<SessionRepoEntity> changes, string? cliProfile, TaskStopRegistry.Lease stopLease)
    {
        using var _stop = stopLease;
        try
        {
            using var scope = scopes.CreateScope();
            using var _identity = AgentOs.SharedKernel.Identity.AmbientIdentity.Push(tenantId, session.MemberUserId, session.Id);
            var sessRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var tester = scope.ServiceProvider.GetRequiredService<IAcceptanceAgent>();
            var prs = scope.ServiceProvider.GetRequiredService<IPrCreationService>();
            var body = await ReadTicketBodyAsync(scope, board, tenantId, session, pat).ConfigureAwait(false) ?? "";
            foreach (var change in changes)
            {
                var repoLabel = $"{change.Owner}/{change.Repo}";
                var verdict = await tester.RunAsync(new AcceptanceRequest(session.Id, tenantId, session.MemberUserId,
                    new WorkRepo(change.Id, change.Owner, change.Repo, change.DefaultBranch, change.BranchName),
                    session.IssueNumber ?? 0, session.Title, body, cliProfile), stopLease.Token).ConfigureAwait(false);
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
                await sessRepo.UpdateRepoRunResultAsync(tenantId, change.Id, verdict.Passed ? "Accepted" : "Rejected", change.BranchName, change.PrUrl,
                    verdict.Passed ? null : AgentOs.Modules.Pipeline.Agents.AcceptanceAgent.Findings(verdict)).ConfigureAwait(false);
                feed.Publish(new SessionRunEvent(tenantId, session.Id, SessionRunEventKind.RepoDone,
                    verdict.Passed ? $"{repoLabel} — passed testing" : $"{repoLabel} — failed testing: {verdict.Summary}",
                    DateTimeOffset.UtcNow, repoLabel, verdict.Passed ? "Accepted" : "Rejected", change.PrUrl));
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

    /// <summary>The number of a pull request from its URL (…/pull/42).</summary>
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

            // Ask for changes: each repo that already has a pull request continues on its branch.
            var workRepos = children
                .Select(c => new WorkRepo(c.Id, c.Owner, c.Repo, c.DefaultBranch,
                    change is not null && !string.IsNullOrEmpty(c.PrUrl) ? c.BranchName : null))
                .ToList();
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
                ChangeRequest: change);

            var workResult = await agent.RunAsync(issueRequest, stopLease.Token).ConfigureAwait(false);

            // One PR per repo that pushed a branch; each repo's result is written back on its own.
            var anyFailure = false;
            foreach (var outcome in workResult.Repos)
            {
                var repoLabel = $"{outcome.Owner}/{outcome.Repo}";
                if (!outcome.Ok)
                {
                    anyFailure = true;
                    await sessRepo.UpdateRepoRunResultAsync(tenantId, outcome.SessionRepoId,
                        "Failed", outcome.BranchName, null, outcome.Error ?? "Agent failed.").ConfigureAwait(false);
                    Publish(SessionRunEventKind.RepoDone, $"{repoLabel} — failed: {outcome.Error}", repoLabel, "Failed");
                    continue;
                }

                // A follow-up pushed more commits to an open pull request: nothing new to open.
                var child = children.FirstOrDefault(c => c.Id == outcome.SessionRepoId);
                if (change is not null && !string.IsNullOrEmpty(child?.PrUrl))
                {
                    await sessRepo.UpdateRepoRunResultAsync(tenantId, outcome.SessionRepoId,
                        "Done", outcome.BranchName, child.PrUrl, null).ConfigureAwait(false);
                    Publish(SessionRunEventKind.RepoDone, $"{repoLabel} — proposed change updated", repoLabel, "Done", child.PrUrl);
                    continue;
                }

                var descriptor = RepoDescriptor(board, tenantId, outcome.Owner, outcome.Repo, child?.DefaultBranch ?? "main", pat);
                try
                {
                    var prResult = await prService.CreateFromBranchAsync(
                        descriptor,
                        outcome.BranchName,
                        PullRequestText.Title(session.IssueNumber, session.Title, session.TicketType),
                        PullRequestText.Body(session.IssueNumber, outcome.Summary)).ConfigureAwait(false);

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
            await sessRepo.UpdateRunResultAsync(tenantId, session.Id, "Failed", null, reason).ConfigureAwait(false);
            Publish(SessionRunEventKind.Failed, reason);
            return;
        }

        var primary = children.Count > 0 ? children[0] : null;
        if (primary is null)
        {
            const string noRepo = "Quality run has no target repo — recreate the session from a board ticket.";
            await sessRepo.UpdateRunResultAsync(tenantId, session.Id, "Failed", null, noRepo).ConfigureAwait(false);
            Publish(SessionRunEventKind.Failed, noRepo);
            return;
        }
        var repoLabel = $"{primary.Owner}/{primary.Repo}";

        // Heavy services resolved lazily, inside this background scope.
        var pipeline = scope.ServiceProvider.GetRequiredService<IPipelineClient>();
        var prService = scope.ServiceProvider.GetRequiredService<IGitHubPrService>();

        Publish(SessionRunEventKind.Running, $"Quality engine — 5-agent pipeline on {repoLabel}.", repoLabel);

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
            await sessRepo.UpdateRepoRunResultAsync(tenantId, primary.Id, "Failed", null, null, msg).ConfigureAwait(false);
            await sessRepo.UpdateRunResultAsync(tenantId, session.Id, "Failed", null, msg).ConfigureAwait(false);
            Publish(SessionRunEventKind.Failed, $"{repoLabel} — {msg}", repoLabel, "Failed");
            return;
        }

        // QA gate: only a result QA PASSED on real model output becomes a PR; anything else is Blocked with the reason.
        var gate = PrGate.Evaluate(result);
        if (!gate.Allowed)
        {
            await sessRepo.UpdateRepoRunResultAsync(tenantId, primary.Id, "Blocked", null, null, gate.Reason).ConfigureAwait(false);
            await sessRepo.RecomputeSessionStatusForTenantAsync(tenantId, session.Id).ConfigureAwait(false);
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
            await sessRepo.UpdateRepoRunResultAsync(tenantId, primary.Id, "Done", pr.Branch, pr.HtmlUrl, null).ConfigureAwait(false);
            await sessRepo.RecomputeSessionStatusForTenantAsync(tenantId, session.Id).ConfigureAwait(false);
            Publish(SessionRunEventKind.Done, $"{repoLabel} — PR opened.", repoLabel, "Done", pr.HtmlUrl);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException
                                   or InvalidOperationException or ArgumentException or System.IO.IOException or TimeoutException)
        {
            await sessRepo.UpdateRepoRunResultAsync(tenantId, primary.Id, "Failed", null, null, $"PR creation failed: {ex.Message}").ConfigureAwait(false);
            await sessRepo.UpdateRunResultAsync(tenantId, session.Id, "Failed", null, $"PR creation failed: {ex.Message}").ConfigureAwait(false);
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
