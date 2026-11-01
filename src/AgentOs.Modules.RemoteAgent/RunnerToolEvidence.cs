// Governance of a CLI run on a member's computer — the server half. The runner reports every tool call its CLI made
// (CliToolCalls.cs on the runner); the broker accepted the batch (right connection, tracked run, within its cap,
// sanitised); this records each call through IToolInvocationLog as one evidence row: the run (request) id, the task
// (session), tenant, agent role, runner id, origin "member-computer", and the policy that decided — the CLI's scoped
// permissions on that machine, not the server's IToolPolicy. Refusals are recorded too (Denied). Best-effort: evidence
// never fails the run, and the buffered log keeps the write off the hub's receive loop.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>Turns an accepted batch of a runner's tool calls into evidence rows.</summary>
internal static class RunnerToolEvidence
{
    /// <summary>The evidence row of one call.</summary>
    public static ToolInvocationEvidence ToEvidence(RunnerToolCallBatch batch, RunnerToolEvent call)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(call);
        return new ToolInvocationEvidence(
            CallId: call.ToolUseId,
            ToolName: call.Tool,
            TenantId: batch.Context.TenantId,
            RunId: batch.RequestId,
            Input: JsonSerializer.Serialize(new { summary = call.Summary }),
            Output: Outcome(call, batch.Permissions),
            IsError: !call.Ok || call.Denied,
            StartedUtc: call.StartedUtc,
            FinishedUtc: call.StartedUtc + TimeSpan.FromMilliseconds(call.DurationMs),
            SessionId: batch.Context.SessionId)
        {
            Origin = ToolEvidenceOrigins.MemberComputer,
            AgentRole = batch.Context.AgentRole,
            RunnerId = batch.RunnerId == Guid.Empty ? null : batch.RunnerId.ToString("D"),
            PolicySource = RunnerPermissions.PolicySource(batch.Permissions),
            Denied = call.Denied,
        };
    }

    /// <summary>The row's plain-language outcome.</summary>
    public static string Outcome(RunnerToolEvent call, string permissions)
    {
        ArgumentNullException.ThrowIfNull(call);
        var who = RunnerPermissions.Normalize(permissions) switch
        {
            RunnerPermissions.Scoped => "the computer's scoped permissions",
            RunnerPermissions.Yolo => "nothing — the computer lets the CLI skip its permission checks (REMOTE_AGENT_YOLO)",
            RunnerPermissions.Custom => "the CLI flags set on the computer (REMOTE_AGENT_ARGS)",
            _ => "the CLI's default permissions",
        };
        if (call.Denied) { return $"Refused by {who} — it never ran."; }
        return call.Ok
            ? $"Ran on the member's computer (allowed by {who})."
            : $"Ran on the member's computer and reported an error (allowed by {who}).";
    }

    /// <summary>Record every call of <paramref name="batch"/>. Never throws.</summary>
    public static async Task RecordAsync(IToolInvocationLog? log, RunnerToolCallBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (log is null) { return; }
        foreach (var call in batch.Calls)
        {
            try
            {
                await log.AppendAsync(ToEvidence(batch, call), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex; // evidence is best-effort here: the buffered sink counts and logs what it could not persist
            }
        }
    }
}
