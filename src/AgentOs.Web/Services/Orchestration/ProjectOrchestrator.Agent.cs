// Autopilot's decision step. By default the Orchestrator agent decides (Settings › Autopilot › Decided by): it gets the
// board's whole state and every legal move the rules allow — uncapped — picks the moves and their order, retries stuck
// tasks with guidance and asks a person precise questions. The rules stay the guardrail: only legal moves can be
// picked, AutopilotPlanner.WithinCaps applies the slot caps to its picks, and retries are bounded per task (Settings ›
// Autopilot: fix rounds + Orchestrator retries, against the task's persisted fix rounds). The agent
// is asked only when the board changed; when it can't answer (no computer, timeout), that pass follows the rules.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.Web.Services.BoardTasks;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Web.Services.Orchestration;

public sealed partial class ProjectOrchestrator
{
    /// <summary>The retries the agent may still spend on a task: its persisted fix rounds (every change sent back to the
    /// Coding agent, by anyone) count against the rules' rounds plus the agent's own (Settings › Autopilot) — so a
    /// restart grants nothing new.</summary>
    internal static int RetriesLeft(int fixRounds, AutopilotLimits limits) =>
        Math.Clamp(limits.MaxFixRounds + limits.MaxAgentRetries - fixRounds, 0, limits.MaxAgentRetries);

    private readonly ConcurrentDictionary<Guid, string> _lastSignature = new();
    private readonly ConcurrentDictionary<Guid, string> _asked = new();
    private readonly ConcurrentDictionary<Guid, bool> _agentDown = new();

    private async Task<IReadOnlyList<AutopilotAction>> DecideAsync(WorkspaceEntity board, AutopilotMode mode, string member, string pat,
        IReadOnlyList<BoardTicket> tickets, IReadOnlyList<PlanTicket> planTickets, IReadOnlyList<PlanTask> planTasks,
        IReadOnlyList<RemoteSessionEntity> sessions, ILookup<Guid, SessionRepoEntity> repos, bool canTest, AutopilotLimits limits,
        IServiceProvider sp, CancellationToken ct)
    {
        var tenant = board.TenantId;
        var decidedBy = await sp.GetRequiredService<ISettings>().GetAsync(AutopilotSettings.DecidedByKey, tenant, ct).ConfigureAwait(false);
        // The agent answers on the member's computer: without one, the rules decide (and nothing gets tested anyway).
        if (!AutopilotSettings.AgentDecides(decidedBy.Value) || !canTest)
        {
            return AutopilotPlanner.Decide(mode, planTickets, planTasks, canTest, limits);
        }

        var candidates = AutopilotPlanner.Decide(mode, planTickets, planTasks, canTest, limits.Uncapped);
        var running = planTasks.Count(t => t.Status == "Running");
        var testing = planTasks.Count(t => t.OpenChanges.Any(c => c.Testing == ChangeTesting.Testing));
        // Work a plan limit cut short is picked up by rule, first — it isn't a decision, and must not wait on one.
        var resumes = candidates.OfType<AutopilotAction.ResumeTask>().ToList();
        var stuck = await WithAnswersAsync(StuckTasks(tickets, planTickets, sessions, repos, candidates, limits), board, pat, sessions, sp, ct).ConfigureAwait(false);
        var moves = candidates.Where(a => a is not AutopilotAction.NeedsPerson and not AutopilotAction.ResumeTask)
            .Select((a, i) => (Id: "M" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Action: a)).ToList();
        var epics = await EpicsAsync(board, tenant, pat, tickets, sp, ct).ConfigureAwait(false);
        if (moves.Count == 0 && stuck.Count == 0 && epics.Count == 0) { return AutopilotPlanner.WithinCaps(resumes, running, testing, limits); }

        var freeSlots = Math.Max(0, limits.MaxParallel - running - resumes.Count);
        // Ask only when something changed: the same picture gets the same answer (and costs the member's quota).
        var signature = Signature(moves.Select(m => m.Action.Why), stuck.Select(s => $"{s.IssueNumber}:{s.Situation}:{s.RetriesLeft}:{s.PersonAnswer}")
            .Concat(epics.Select(e => $"epic {e.IssueNumber}"))
            .Concat(tickets.Where(t => string.Equals(t.State, "CLOSED", StringComparison.OrdinalIgnoreCase)).Select(t => $"closed {t.Number}")), freeSlots);
        if (_lastSignature.TryGetValue(board.Id, out var last) && last == signature) { return AutopilotPlanner.WithinCaps(resumes, running, testing, limits); }

        var recent = (await log.RecentAsync(tenant, board.Id, 12).ConfigureAwait(false)).Select(e => e.Text).ToList();
        var brief = new OrchestratorBrief(board.Id, tenant, member, board.Name, mode.ToString(),
            Snapshot(tickets, planTickets, planTasks, sessions),
            moves.Select(m => new OrchestratorMove(m.Id, KindOf(m.Action), IssueOf(m.Action), m.Action.Why)).ToList(),
            stuck, freeSlots, recent) { Epics = epics };
        // Metrics first: the decision's LLM call is an agent run in Insights like every other agent's.
        OrchestratorDecision decision;
        var decidedAt = DateTimeOffset.UtcNow;
        using (var calls = AgentOs.Domain.Llm.LlmCallScope.Begin())
        {
            decision = await sp.GetRequiredService<IAutopilotOrchestrator>().DecideAsync(brief, ct).ConfigureAwait(false);
            await tasks.SaveAgentRunAsync(tenant, member, "Orchestrator", $"Orchestrator · {board.Name}", decidedAt, calls.Calls).ConfigureAwait(false);
        }
        if (!decision.Ran)
        {
            // The member's plan hit its limit while the agent decided: the pass waits (StepAsync logs it), no fallback.
            if (planLimits.Current(tenant, member) is not null) { return []; }
            if (_agentDown.TryAdd(board.Id, true))
            {
                log.Add(tenant, board.Id, $"The Orchestrator agent couldn't decide ({decision.Error}) — following the rules until it can.");
            }
            return AutopilotPlanner.Decide(mode, planTickets, planTasks, canTest, limits);
        }
        _agentDown.TryRemove(board.Id, out _);
        _lastSignature[board.Id] = signature;
        if (!string.IsNullOrWhiteSpace(decision.Reasoning)) { log.Add(tenant, board.Id, "Orchestrator: " + decision.Reasoning); }

        var byId = moves.ToDictionary(m => m.Id, m => m.Action, StringComparer.Ordinal);
        var picked = AutopilotPlanner.WithinCaps(resumes.Concat(decision.MoveIds.Select(id => byId[id])), running, testing, limits).ToList();
        running += picked.Count(AutopilotPlanner.TakesSlot);

        // Stuck tasks: retried with the agent's guidance (bounded), asked about, or — when it did neither — marked for a
        // person the way the rules would.
        var retried = new HashSet<int>();
        foreach (var retry in decision.Retries)
        {
            if (running >= limits.MaxParallel) { break; }
            var task = stuck.First(s => s.IssueNumber == retry.IssueNumber);
            // A guided retry is a change request: it counts as one more (persisted) fix round of the task.
            var started = await tasks.StartAsync(tenant, task.SessionId, changeRequest: retry.Guidance, ct: ct).ConfigureAwait(false);
            if (started != TaskStart.Started) { continue; }
            retried.Add(task.IssueNumber);
            running++;
            log.Add(tenant, board.Id, $"Orchestrator started #{task.IssueNumber} again with guidance: {Short(retry.Guidance)}");
            // The reply is used up: a signed acknowledgement after it, so a failed attempt leads to a new question.
            if (task.PersonAnswer is not null)
            {
                await CommentOnTicketAsync(board, pat, sessions.First(s => s.Id == task.SessionId),
                    PersonAnswers.Acknowledgement("trying again with your answer."), sp, ct).ConfigureAwait(false);
            }
        }
        // A person said a ticket is done or no longer wanted: close it (with their reason) and the task with it.
        foreach (var close in decision.Closes)
        {
            // An epic whose scope is delivered: closed with the agent's summary — no person needed (reopen to undo).
            if (stuck.All(s => s.IssueNumber != close.IssueNumber))
            {
                var epic = tickets.FirstOrDefault(t => t.Number == close.IssueNumber);
                if (epic is { RepoOwner: { } eo, RepoName: { } en } && await sp.GetRequiredService<IPrCreationService>().CloseAsync(
                        BoardTaskService.RepoDescriptor(board, tenant, eo, en, "main", pat), close.IssueNumber,
                        ClosedByAgent(close.Reason), ct).ConfigureAwait(false))
                {
                    retried.Add(close.IssueNumber);
                    log.Add(tenant, board.Id, $"Closed epic #{close.IssueNumber}: {close.Reason}");
                }
                continue;
            }
            var task = stuck.First(s => s.IssueNumber == close.IssueNumber);
            var session = sessions.First(s => s.Id == task.SessionId);
            // Thank the person only when they answered; a ticket the agent closes on its own (an epic with a stuck task,
            // its scope delivered) says so, like an epic without one.
            var closing = task.PersonAnswer is null
                ? ClosedByAgent(close.Reason)
                : PersonAnswers.Acknowledgement($"closing #{close.IssueNumber}: {close.Reason}");
            if (!await CloseTicketAsync(board, pat, session, closing, sp, ct).ConfigureAwait(false))
            {
                continue;
            }
            await sp.GetRequiredService<AgentOs.Modules.Sessions.Persistence.ISessionRepository>()
                .UpdateRunResultAsync(tenant, session.Id, "Closed", session.PrUrl, null, ct).ConfigureAwait(false);
            retried.Add(close.IssueNumber);
            log.Add(tenant, board.Id, task.PersonAnswer is null
                ? $"Closed #{close.IssueNumber}: {close.Reason}"
                : $"Closed #{close.IssueNumber} after your answer: {close.Reason}");
        }
        foreach (var question in decision.Questions)
        {
            var task = stuck.First(s => s.IssueNumber == question.IssueNumber);
            if (_asked.TryGetValue(task.SessionId, out var asked) && asked == question.Question) { continue; }
            await AskAsync(board, pat, sessions.First(s => s.Id == task.SessionId), question.Question, sp, ct).ConfigureAwait(false);
            _asked[task.SessionId] = question.Question;
            log.Add(tenant, board.Id, $"Asked you about #{task.IssueNumber}: {question.Question}");
        }
        var handled = retried.Concat(decision.Questions.Select(q => q.IssueNumber)).ToHashSet();
        picked.AddRange(candidates.OfType<AutopilotAction.NeedsPerson>().Where(n => !handled.Contains(n.IssueNumber)));
        return picked;
    }

    /// <summary>The comment on a ticket the Orchestrator agent closes on its own (nobody was asked): its reason, and how to undo it.</summary>
    internal static string ClosedByAgent(string reason) =>
        $"Closing — {reason}\n\n<sub>Closed by the AgentOS.NET Orchestrator agent: everything this ticket covers is delivered. Reopen it if anything is missing.</sub>\n";

    private readonly ConcurrentDictionary<(Guid Board, int Issue), (DateTimeOffset At, string Body)> _epicBodies = new();

    // Open umbrella tickets (labelled for a person) with their text — read at most every 10 minutes each.
    private async Task<List<OrchestratorEpic>> EpicsAsync(WorkspaceEntity board, string tenant, string pat, IReadOnlyList<BoardTicket> tickets,
        IServiceProvider sp, CancellationToken ct)
    {
        var epics = new List<OrchestratorEpic>();
        var resolver = sp.GetRequiredService<ISourceProviderResolver>();
        if (!resolver.TryResolve(board.Kind, out var provider) || provider is null) { return epics; }
        foreach (var t in tickets.Where(t => t.Number.HasValue && t.RepoOwner is not null && t.RepoName is not null
                     && !string.Equals(t.State, "CLOSED", StringComparison.OrdinalIgnoreCase) && t.Labels.Contains(StandardLabels.NeedsHuman)))
        {
            var number = t.Number.GetValueOrDefault();
            if (!_epicBodies.TryGetValue((board.Id, number), out var cached) || DateTimeOffset.UtcNow - cached.At > TimeSpan.FromMinutes(10))
            {
                string body;
                try
                {
                    body = await provider.ReadTicketBodyAsync(BoardTaskService.RepoDescriptor(board, tenant, t.RepoOwner!, t.RepoName!, "main", pat), number, ct)
                        .ConfigureAwait(false) ?? "";
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Octokit.ApiException or InvalidOperationException or TaskCanceledException)
                {
                    continue;
                }
                cached = (DateTimeOffset.UtcNow, body);
                _epicBodies[(board.Id, number)] = cached;
            }
            epics.Add(new OrchestratorEpic(number, t.Title, cached.Body));
        }
        return epics;
    }

    // Each stuck task's ticket is read for a person's reply to the agent's last question (one comments call per task).
    private static async Task<List<OrchestratorStuckTask>> WithAnswersAsync(List<OrchestratorStuckTask> stuck, WorkspaceEntity board, string pat,
        IReadOnlyList<RemoteSessionEntity> sessions, IServiceProvider sp, CancellationToken ct)
    {
        var prs = sp.GetRequiredService<IPrCreationService>();
        for (var i = 0; i < stuck.Count; i++)
        {
            var session = sessions.First(s => s.Id == stuck[i].SessionId);
            if (Ticket(board, pat, session) is not { } ticket) { continue; }
            var answer = PersonAnswers.Latest(await prs.ListCommentsAsync(ticket.Repo, ticket.Issue, ct).ConfigureAwait(false));
            if (answer is null) { continue; }
            // New information from a person is worth one more attempt even when the automatic ones are used up.
            stuck[i] = stuck[i] with { PersonAnswer = answer, RetriesLeft = Math.Max(1, stuck[i].RetriesLeft) };
        }
        return stuck;
    }

    // The ticket's repo and number, when the task has a ticket.
    private static (WorkspaceDescriptor Repo, int Issue)? Ticket(WorkspaceEntity board, string pat, RemoteSessionEntity session) =>
        session.IssueNumber is not { } issue || string.IsNullOrWhiteSpace(session.RepoOwner) || string.IsNullOrWhiteSpace(session.RepoName)
            ? null
            : (BoardTaskService.RepoDescriptor(board, board.TenantId, session.RepoOwner, session.RepoName, session.RepoDefaultBranch ?? "main", pat), issue);

    private static async Task CommentOnTicketAsync(WorkspaceEntity board, string pat, RemoteSessionEntity session, string body, IServiceProvider sp, CancellationToken ct)
    {
        if (Ticket(board, pat, session) is { } t)
        {
            await sp.GetRequiredService<IPrCreationService>().CommentAsync(t.Repo, t.Issue, body, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> CloseTicketAsync(WorkspaceEntity board, string pat, RemoteSessionEntity session, string comment, IServiceProvider sp, CancellationToken ct) =>
        Ticket(board, pat, session) is { } t
        && await sp.GetRequiredService<IPrCreationService>().CloseAsync(t.Repo, t.Issue, comment, ct).ConfigureAwait(false);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _supersededSweep = new();

    /// <summary>
    /// An attempt that ended without AgentOS learning its pull request (a run cut short after it pushed) left that PR
    /// open, and the next attempt opened another on a fresh branch: two PRs for one ticket (seen on the lab: #53 next to
    /// the merged #56, #54 next to #55). Every 10 minutes per board, an open PR on an AgentOS attempt branch
    /// (issue-N-ai-fix…) is closed as superseded when AgentOS knows a newer PR for the same ticket. Only attempt
    /// branches are touched; best-effort.
    /// </summary>
    private async Task CloseSupersededAsync(WorkspaceEntity board, string pat, IReadOnlyList<RemoteSessionEntity> sessions,
        ILookup<Guid, SessionRepoEntity> repos, IServiceProvider sp, CancellationToken ct)
    {
        if (_supersededSweep.TryGetValue(board.Id, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(10)) { return; }
        _supersededSweep[board.Id] = DateTimeOffset.UtcNow;
        var prs = sp.GetRequiredService<IPrCreationService>();
        // The pull request AgentOS tracks for each (repo, ticket): the one its task points at now.
        var current = new Dictionary<(string Owner, string Repo, int Issue), (int Pr, SessionRepoEntity Repo)>();
        var tracked = sessions.Where(s => s.IssueNumber.HasValue)
            .SelectMany(s => repos[s.Id].Where(r => r.Status is not "Failed" && PrNumber(r.PrUrl).HasValue)
                .Select(r => (Issue: s.IssueNumber.GetValueOrDefault(), Pr: PrNumber(r.PrUrl).GetValueOrDefault(), Repo: r)));
        foreach (var t in tracked) { current.TryAdd((t.Repo.Owner, t.Repo.Repo, t.Issue), (t.Pr, t.Repo)); }
        foreach (var repo in current.Values.Select(c => c.Repo).DistinctBy(r => (r.Owner, r.Repo)))
        {
            var desc = BoardTaskService.RepoDescriptor(board, board.TenantId, repo.Owner, repo.Repo, repo.DefaultBranch, pat);
            foreach (var open in await prs.ListOpenPullRequestsAsync(desc, "issue-", ct).ConfigureAwait(false))
            {
                if (PersonAnswers.TicketOfAttemptBranch(open.HeadBranch) is not { } issue
                    || !current.TryGetValue((repo.Owner, repo.Repo, issue), out var known) || known.Pr == open.Number)
                {
                    continue;
                }
                var closed = await prs.CloseAsync(desc, open.Number,
                    $"Superseded by #{known.Pr} — a later attempt at #{issue}.\n\n<sub>Closed by the AgentOS.NET Orchestrator: an earlier attempt's pull request that a newer one replaces.</sub>\n",
                    ct).ConfigureAwait(false);
                if (closed) { log.Add(board.TenantId, board.Id, $"Closed PR #{open.Number}: an earlier attempt at #{issue}, superseded by #{known.Pr}."); }
            }
        }
    }

    // Tasks that stopped: couldn't finish, or blocked for a person — plus those the rules give up on this very pass.
    // Work a plan limit cut short isn't stuck (it resumes by rule after the reset), so no retry is spent on it.
    private List<OrchestratorStuckTask> StuckTasks(IReadOnlyList<BoardTicket> tickets, IReadOnlyList<PlanTicket> planTickets,
        IReadOnlyList<RemoteSessionEntity> sessions, ILookup<Guid, SessionRepoEntity> repos, IReadOnlyList<AutopilotAction> candidates,
        AutopilotLimits limits)
    {
        var openTickets = planTickets.Where(t => t.Open).Select(t => t.Number).ToHashSet();
        var titles = tickets.Where(t => t.Number.HasValue).GroupBy(t => t.Number.GetValueOrDefault()).ToDictionary(g => g.Key, g => g.First().Title);
        var stuck = new List<OrchestratorStuckTask>();
        foreach (var s in sessions.Where(s => s.Status is "Failed" or "Blocked" && !workflows.IsRunning(s.TenantId, s.Id)
                     && s.IssueNumber.HasValue && openTickets.Contains(s.IssueNumber.GetValueOrDefault())
                     && !(s.Status == "Failed" && CutShortByPlanLimit(s, repos))))
        {
            var issue = s.IssueNumber.GetValueOrDefault();
            var why = string.Join(" | ", repos[s.Id].Select(r => r.Error).Prepend(s.Error).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct());
            stuck.Add(new OrchestratorStuckTask(s.Id, issue, titles.GetValueOrDefault(issue, s.Title),
                $"{(s.Status == "Failed" ? "Couldn't finish" : "Waiting for a person")}: {(why.Length == 0 ? "no reason recorded" : why)}",
                RetriesLeft(s.FixRounds, limits)));
        }
        foreach (var n in candidates.OfType<AutopilotAction.NeedsPerson>().Where(n => stuck.All(s => s.SessionId != n.SessionId)))
        {
            var s = sessions.First(x => x.Id == n.SessionId);
            var findings = string.Join(" | ", repos[s.Id].Select(r => r.Error).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct());
            stuck.Add(new OrchestratorStuckTask(s.Id, n.IssueNumber, titles.GetValueOrDefault(n.IssueNumber, s.Title),
                $"{n.Reason} {findings}".Trim(), RetriesLeft(s.FixRounds, limits)));
        }
        return stuck;
    }

    // The board as the agent reads it: tickets with their dependencies, tasks with each proposed change's state.
    internal static string Snapshot(IReadOnlyList<BoardTicket> tickets, IReadOnlyList<PlanTicket> planTickets,
        IReadOnlyList<PlanTask> planTasks, IReadOnlyList<RemoteSessionEntity> sessions)
    {
        var sb = new StringBuilder();
        var plan = planTickets.ToDictionary(t => t.Number);
        sb.Append("Tickets:\n");
        foreach (var t in tickets.Where(t => t.Number.HasValue).OrderBy(t => t.Number))
        {
            var number = t.Number.GetValueOrDefault();
            var p = plan.GetValueOrDefault(number);
            sb.Append('#').Append(t.Number).Append(' ').Append(p?.Open == false ? "[closed]" : "[open]")
              .Append(" [").Append(string.Join(", ", t.Labels)).Append(']');
            if (p is { DependsOn.Count: > 0 }) { sb.Append(" depends on ").Append(string.Join(", ", p.DependsOn.Select(d => "#" + d))); }
            if (p is { HasLiveTask: true }) { sb.Append(" (has a task)"); }
            sb.Append(" — ").Append(t.Title).Append('\n');
        }
        sb.Append("\nTasks in progress:\n");
        foreach (var task in planTasks)
        {
            sb.Append('#').Append(task.IssueNumber).Append(' ').Append(task.Status).Append(", fix rounds used ").Append(task.FixRounds);
            // Its ticket flow leaves steps out: the agent must not expect (or ask for) a review or test it won't get.
            if (task.Flow.Skipped.Count > 0) { sb.Append(", ticket flow ").Append(task.Flow.Summary); }
            sb.Append('\n');
            foreach (var c in task.OpenChanges)
            {
                sb.Append("  PR #").Append(c.PrNumber).Append(": checks ").Append(c.Checks).Append(", review ").Append(c.Review)
                  .Append(", testing ").Append(c.Testing).Append(", merge ").Append(c.Merge);
                if (!string.IsNullOrWhiteSpace(c.Findings)) { sb.Append(", findings: ").Append(Short(c.Findings, 400)); }
                if (!string.IsNullOrWhiteSpace(c.FailedReport)) { sb.Append(", failed checks: ").Append(Short(c.FailedReport, 400)); }
                sb.Append('\n');
            }
        }
        foreach (var s in sessions.Where(s => s.Status is "Failed" or "Blocked"))
        {
            sb.Append('#').Append(s.IssueNumber).Append(' ').Append(s.Status).Append(": ").Append(Short(s.Error ?? "", 300)).Append('\n');
        }
        return sb.ToString();
    }

    // A question for a person goes on the ticket, where they will see it (and the Autopilot log says so).
    private static async Task AskAsync(WorkspaceEntity board, string pat, RemoteSessionEntity session, string question, IServiceProvider sp, CancellationToken ct)
    {
        if (session.IssueNumber is not { } issue || string.IsNullOrWhiteSpace(session.RepoOwner) || string.IsNullOrWhiteSpace(session.RepoName)) { return; }
        var repo = BoardTaskService.RepoDescriptor(board, board.TenantId, session.RepoOwner, session.RepoName, session.RepoDefaultBranch ?? "main", pat);
        await sp.GetRequiredService<IPrCreationService>().CommentAsync(repo, issue,
            $"### ❓ AgentOS.NET needs you\n\n{question}\n\n<sub>Asked by the AgentOS.NET Orchestrator agent. Answer here or in the task, then press Try again.</sub>\n",
            ct).ConfigureAwait(false);
    }

    internal static string Signature(IEnumerable<string> moves, IEnumerable<string> stuck, int freeSlots)
    {
        var text = string.Join('\n', moves.Order(StringComparer.Ordinal)) + "\n--\n" + string.Join('\n', stuck.Order(StringComparer.Ordinal))
            + "\n--\n" + freeSlots.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string KindOf(AutopilotAction a) => a switch
    {
        AutopilotAction.StartTicket => "start",
        AutopilotAction.ResumeTask => "resume",
        AutopilotAction.FixChecks => "fix",
        AutopilotAction.RunReview => "review",
        AutopilotAction.RunTests => "test",
        AutopilotAction.Merge => "merge",
        AutopilotAction.UpdateBranch => "update-branch",
        _ => "other",
    };

    private static int IssueOf(AutopilotAction a) => a switch
    {
        AutopilotAction.StartTicket x => x.Number,
        AutopilotAction.ResumeTask x => x.IssueNumber,
        AutopilotAction.FixChecks x => x.IssueNumber,
        AutopilotAction.RunReview x => x.IssueNumber,
        AutopilotAction.RunTests x => x.IssueNumber,
        AutopilotAction.Merge x => x.IssueNumber,
        AutopilotAction.UpdateBranch x => x.IssueNumber,
        AutopilotAction.NeedsPerson x => x.IssueNumber,
        _ => 0,
    };

    private static string Short(string s, int max = 160)
    {
        var one = s.Replace('\n', ' ').Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }
}
