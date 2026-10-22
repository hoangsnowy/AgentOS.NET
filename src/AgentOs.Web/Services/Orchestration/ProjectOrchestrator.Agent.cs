// Autopilot's decision step. By default the Orchestrator agent decides (Settings › Autopilot › Decided by): it gets the
// board's whole state and every legal move the rules allow — uncapped — picks the moves and their order, retries stuck
// tasks with guidance and asks a person precise questions. The rules stay the guardrail: only legal moves can be
// picked, AutopilotPlanner.WithinCaps applies the slot caps to its picks, and retries are bounded per task. The agent
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
    /// <summary>Agent-led retries per stuck task (beyond the rules' fix rounds).</summary>
    internal const int MaxAgentRetries = 2;

    private readonly ConcurrentDictionary<Guid, string> _lastSignature = new();
    private readonly ConcurrentDictionary<Guid, int> _agentRetries = new();
    private readonly ConcurrentDictionary<Guid, string> _asked = new();
    private readonly ConcurrentDictionary<Guid, bool> _agentDown = new();

    private async Task<IReadOnlyList<AutopilotAction>> DecideAsync(WorkspaceEntity board, AutopilotMode mode, string member, string pat,
        IReadOnlyList<BoardTicket> tickets, IReadOnlyList<PlanTicket> planTickets, IReadOnlyList<PlanTask> planTasks,
        IReadOnlyList<RemoteSessionEntity> sessions, ILookup<Guid, SessionRepoEntity> repos, bool canTest, IServiceProvider sp, CancellationToken ct)
    {
        var tenant = board.TenantId;
        var decidedBy = await sp.GetRequiredService<ISettings>().GetAsync(AutopilotSettings.DecidedByKey, tenant, ct).ConfigureAwait(false);
        // The agent answers on the member's computer: without one, the rules decide (and nothing gets tested anyway).
        if (!AutopilotSettings.AgentDecides(decidedBy.Value) || !canTest)
        {
            return AutopilotPlanner.Decide(mode, planTickets, planTasks, canTest);
        }

        var candidates = AutopilotPlanner.Decide(mode, planTickets, planTasks, canTest, int.MaxValue, int.MaxValue);
        var running = planTasks.Count(t => t.Status == "Running");
        var testing = planTasks.Count(t => t.OpenChanges.Any(c => c.Testing == ChangeTesting.Testing));
        var stuck = StuckTasks(tickets, planTickets, sessions, repos, candidates);
        var moves = candidates.Where(a => a is not AutopilotAction.NeedsPerson)
            .Select((a, i) => (Id: "M" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Action: a)).ToList();
        if (moves.Count == 0 && stuck.Count == 0) { return []; }

        var freeSlots = Math.Max(0, AutopilotPlanner.MaxParallel - running);
        // Ask only when something changed: the same picture gets the same answer (and costs the member's quota).
        var signature = Signature(moves.Select(m => m.Action.Why), stuck.Select(s => $"{s.IssueNumber}:{s.Situation}:{s.RetriesLeft}"), freeSlots);
        if (_lastSignature.TryGetValue(board.Id, out var last) && last == signature) { return []; }

        var recent = (await log.RecentAsync(tenant, board.Id, 12).ConfigureAwait(false)).Select(e => e.Text).ToList();
        var brief = new OrchestratorBrief(board.Id, tenant, member, board.Name, mode.ToString(),
            Snapshot(tickets, planTickets, planTasks, sessions),
            moves.Select(m => new OrchestratorMove(m.Id, KindOf(m.Action), IssueOf(m.Action), m.Action.Why)).ToList(),
            stuck, freeSlots, recent);
        var decision = await sp.GetRequiredService<IAutopilotOrchestrator>().DecideAsync(brief, ct).ConfigureAwait(false);
        if (!decision.Ran)
        {
            if (_agentDown.TryAdd(board.Id, true))
            {
                log.Add(tenant, board.Id, $"The Orchestrator agent couldn't decide ({decision.Error}) — following the rules until it can.");
            }
            return AutopilotPlanner.Decide(mode, planTickets, planTasks, canTest);
        }
        _agentDown.TryRemove(board.Id, out _);
        _lastSignature[board.Id] = signature;
        if (!string.IsNullOrWhiteSpace(decision.Reasoning)) { log.Add(tenant, board.Id, "Orchestrator: " + decision.Reasoning); }

        var byId = moves.ToDictionary(m => m.Id, m => m.Action, StringComparer.Ordinal);
        var picked = AutopilotPlanner.WithinCaps(decision.MoveIds.Select(id => byId[id]), running, testing).ToList();
        running += picked.Count(AutopilotPlanner.TakesSlot);

        // Stuck tasks: retried with the agent's guidance (bounded), asked about, or — when it did neither — marked for a
        // person the way the rules would.
        var retried = new HashSet<int>();
        foreach (var retry in decision.Retries)
        {
            if (running >= AutopilotPlanner.MaxParallel) { break; }
            var task = stuck.First(s => s.IssueNumber == retry.IssueNumber);
            var started = await tasks.StartAsync(tenant, task.SessionId, changeRequest: retry.Guidance, ct: ct).ConfigureAwait(false);
            if (started != TaskStart.Started) { continue; }
            _agentRetries.AddOrUpdate(task.SessionId, 1, (_, n) => n + 1);
            _fixRounds.TryRemove(task.SessionId, out _);
            retried.Add(task.IssueNumber);
            running++;
            log.Add(tenant, board.Id, $"Orchestrator started #{task.IssueNumber} again with guidance: {Short(retry.Guidance)}");
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

    // Tasks that stopped: couldn't finish, or blocked for a person — plus those the rules give up on this very pass.
    private List<OrchestratorStuckTask> StuckTasks(IReadOnlyList<BoardTicket> tickets, IReadOnlyList<PlanTicket> planTickets,
        IReadOnlyList<RemoteSessionEntity> sessions, ILookup<Guid, SessionRepoEntity> repos, IReadOnlyList<AutopilotAction> candidates)
    {
        var openTickets = planTickets.Where(t => t.Open).Select(t => t.Number).ToHashSet();
        var titles = tickets.Where(t => t.Number is not null).GroupBy(t => t.Number!.Value).ToDictionary(g => g.Key, g => g.First().Title);
        var stuck = new List<OrchestratorStuckTask>();
        foreach (var s in sessions.Where(s => s.Status is "Failed" or "Blocked" && s.IssueNumber is { } n && openTickets.Contains(n)))
        {
            var why = string.Join(" | ", repos[s.Id].Select(r => r.Error).Prepend(s.Error).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct());
            stuck.Add(new OrchestratorStuckTask(s.Id, s.IssueNumber!.Value, titles.GetValueOrDefault(s.IssueNumber.Value, s.Title),
                $"{(s.Status == "Failed" ? "Couldn't finish" : "Waiting for a person")}: {(why.Length == 0 ? "no reason recorded" : why)}",
                MaxAgentRetries - _agentRetries.GetValueOrDefault(s.Id)));
        }
        foreach (var n in candidates.OfType<AutopilotAction.NeedsPerson>().Where(n => stuck.All(s => s.SessionId != n.SessionId)))
        {
            var s = sessions.First(x => x.Id == n.SessionId);
            var findings = string.Join(" | ", repos[s.Id].Select(r => r.Error).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct());
            stuck.Add(new OrchestratorStuckTask(s.Id, n.IssueNumber, titles.GetValueOrDefault(n.IssueNumber, s.Title),
                $"{n.Reason} {findings}".Trim(), MaxAgentRetries - _agentRetries.GetValueOrDefault(s.Id)));
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
        foreach (var t in tickets.Where(t => t.Number is not null).OrderBy(t => t.Number))
        {
            var p = plan.GetValueOrDefault(t.Number!.Value);
            sb.Append("#").Append(t.Number).Append(' ').Append(p?.Open == false ? "[closed]" : "[open]")
              .Append(" [").Append(string.Join(", ", t.Labels)).Append(']');
            if (p is { DependsOn.Count: > 0 }) { sb.Append(" depends on ").Append(string.Join(", ", p.DependsOn.Select(d => "#" + d))); }
            if (p is { HasLiveTask: true }) { sb.Append(" (has a task)"); }
            sb.Append(" — ").Append(t.Title).Append('\n');
        }
        sb.Append("\nTasks in progress:\n");
        foreach (var task in planTasks)
        {
            sb.Append("#").Append(task.IssueNumber).Append(' ').Append(task.Status).Append(", fix rounds used ").Append(task.FixRounds).Append('\n');
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
            sb.Append("#").Append(s.IssueNumber).Append(' ').Append(s.Status).Append(": ").Append(Short(s.Error ?? "", 300)).Append('\n');
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
