// Autopilot waits out the member's Claude/Codex plan limit, board by board: while the member's computer reports one
// (IPlanLimits, recorded by the RemoteAgent provider), a pass starts nothing, retries nothing and sends nothing back
// to the Coding agent; the Autopilot activity gets ONE plain line when the wait starts ("…waits until 23:50, nothing
// is lost") and one when it ends. Work the limit cut short is picked up again by the planner (AutopilotAction.Resume).

using System;
using System.Collections.Concurrent;
using AgentOs.Modules.RemoteAgent;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>Per-board memory of a plan-limit wait. Thread-safe.</summary>
public sealed class PlanLimitPause
{
    private readonly ConcurrentDictionary<Guid, PlanLimitHold> _waiting = new();

    /// <summary>
    /// What a pass over <paramref name="boardId"/> may do, given its member's current wait (<paramref name="hold"/>,
    /// null = the plan is open): paused or not, and the line to log — only when the wait starts (or a later reset
    /// replaces it) and when it ends.
    /// </summary>
    public (bool Paused, string? Line) Observe(Guid boardId, PlanLimitHold? hold)
    {
        if (hold is not null)
        {
            var announced = _waiting.TryGetValue(boardId, out var last) && last.UntilUtc == hold.UntilUtc;
            _waiting[boardId] = hold;
            return (true, announced ? null : hold.Sentence);
        }
        return _waiting.TryRemove(boardId, out var ended)
            ? (false, $"Your computer's {ended.Limit.Plan} plan is open again — Autopilot carries on where it stopped.")
            : (false, null);
    }
}
