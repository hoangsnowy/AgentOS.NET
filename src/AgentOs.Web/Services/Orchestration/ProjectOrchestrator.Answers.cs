// A blocked task resumes when its project's maintainer answers on the ticket (TicketAnswers decides). Each pass reads the
// ticket of each Blocked task only — not of every ticket — that Autopilot could resume now: its ticket is open, it isn't
// running, it has a pull request to continue (as Board › Tasks › Ask for changes needs) and a task slot is free. The
// answer is recorded on the task (sessions.sessions.TicketAnswerAtUtc) BEFORE the run starts, so it resumes the task at
// most once, across restarts.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.Web.Services.BoardTasks;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Web.Services.Orchestration;

public sealed partial class ProjectOrchestrator
{
    // Enough of a ticket's newest comments to find an answer (the provider reads ~3× this many, then people's are kept).
    private const int AnswerComments = TicketDiscussion.DefaultMaxComments;

    /// <summary>Resume the board's blocked tasks a maintainer answered on their tickets; true when any was started.</summary>
    private async Task<bool> ResumeAnsweredAsync(WorkspaceEntity board, AutopilotMode mode, string pat, IReadOnlyList<BoardTicket> tickets,
        IReadOnlyList<RemoteSessionEntity> sessions, ILookup<Guid, SessionRepoEntity> repos, AutopilotLimits limits, IServiceProvider sp,
        CancellationToken ct)
    {
        var tenant = board.TenantId;
        var blocked = sessions.Where(s => s.Status == "Blocked" && s.IssueNumber.HasValue
            && !stops.IsRunning(tenant, s.Id) && !workflows.IsRunning(tenant, s.Id) && HasOpenChange(repos[s.Id])).ToList();
        if (blocked.Count == 0) { return false; }
        var settings = sp.GetRequiredService<ISettings>();
        var enabled = await AutopilotSettings.ReadResumeOnTicketAnswerAsync(settings, tenant, ct).ConfigureAwait(false);
        if (!TicketAnswers.Applies(mode, enabled)) { return false; }
        var free = limits.MaxParallel - sessions.Count(s => s.Status == "Running");
        if (free <= 0) { return false; }
        if (!sp.GetRequiredService<ISourceProviderResolver>().TryResolve(board.Kind, out var provider) || provider is null) { return false; }
        var agentDecides = AutopilotSettings.AgentDecides((await settings.GetAsync(AutopilotSettings.DecidedByKey, tenant, ct).ConfigureAwait(false)).Value);
        var openTickets = tickets.Where(t => !string.Equals(t.State, "CLOSED", StringComparison.OrdinalIgnoreCase))
            .Select(t => (t.RepoOwner, t.RepoName, t.Number)).ToHashSet();
        var sessionsRepo = sp.GetRequiredService<ISessionRepository>();

        var resumed = false;
        foreach (var s in blocked.Where(s => openTickets.Contains((s.RepoOwner, s.RepoName, s.IssueNumber))))
        {
            if (free <= 0) { break; }
            // Blocked before its block time was recorded (it was blocked when this feature shipped): only answers written
            // from now on count — an old comment from before it existed must never send it back to the AI.
            if (s.BlockedAtUtc is null && s.TicketAnswerAtUtc is null)
            {
                await sessionsRepo.MarkTicketAnswerForTenantAsync(tenant, s.Id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                continue;
            }
            if (Ticket(board, pat, s) is not { } ticket) { continue; }
            TicketContent content;
            try
            {
                content = await provider.ReadTicketAsync(ticket.Repo, ticket.Issue, AnswerComments, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Octokit.ApiException or InvalidOperationException or TaskCanceledException)
            {
                continue;   // best-effort: the next pass reads it again
            }
            var decision = TicketAnswers.Decide(mode, enabled, agentDecides, s.BlockedAtUtc, s.TicketAnswerAtUtc, s.CreatedAtUtc, content.Comments);
            if (decision is not { Outcome: TicketAnswerOutcome.Resume, AnsweredAt: { } answeredAt }) { continue; }

            // Used up first: whatever happens to the run, this answer never resumes the task again.
            await sessionsRepo.MarkTicketAnswerForTenantAsync(tenant, s.Id, answeredAt, ct).ConfigureAwait(false);
            // Exactly what Ask for changes does: the same branch and pull request, counted as one more fix round — a
            // person's answer gets its run even when the automatic rounds are used up, and grants no new automatic ones.
            var started = await tasks.StartAsync(tenant, s.Id, changeRequest: TicketAnswers.Request(decision.Answers), ct: ct).ConfigureAwait(false);
            if (started != TaskStart.Started)
            {
                log.Add(tenant, board.Id, $"#{ticket.Issue}: the maintainer answered on the ticket, but the task couldn't start again: {started}.");
                continue;
            }
            free--;
            resumed = true;
            log.Add(tenant, board.Id, decision.Answers.Count == 1
                ? $"#{ticket.Issue}: the maintainer answered on the ticket — sent the task back to the AI with the answer (same pull request): {Short(decision.Answers[0].Body)}"
                : $"#{ticket.Issue}: the maintainer answered on the ticket ({decision.Answers.Count} comments) — sent the task back to the AI with them (same pull request).");
            // An answer to the Orchestrator agent's question is acknowledged the way the agent's own retry does it, so the
            // same reply is never read as a fresh answer later.
            if (decision.AnswersAQuestion)
            {
                await CommentOnTicketAsync(board, pat, s, PersonAnswers.Acknowledgement("trying again with your answer."), sp, ct).ConfigureAwait(false);
            }
        }
        return resumed;
    }

    // A pull request to continue on — what Board's "Ask for changes" needs too.
    private static bool HasOpenChange(IEnumerable<SessionRepoEntity> repos) =>
        repos.Any(r => !string.IsNullOrEmpty(r.PrUrl) && !string.IsNullOrEmpty(r.BranchName));
}
