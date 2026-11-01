// Epic E5 — Evidence sink. Every tool invocation writes one entry so the run's audit trail
// covers not just the LLM prompts but every side-effect the model triggered. The default impl
// is in-memory ring buffer (visible via /tools/invocations); production wires an EF-backed
// repository that streams into the tools schema.

using System.Linq;

namespace AgentOs.Domain.Tools;

/// <summary>One row of evidence per tool invocation.</summary>
/// <param name="CallId">Echoes <see cref="ToolInvocationRequest.CallId"/>.</param>
/// <param name="ToolName">Tool that was invoked.</param>
/// <param name="TenantId">Tenant the call was made for.</param>
/// <param name="RunId">Pipeline run correlator, when set.</param>
/// <param name="Input">JSON input as the model sent it.</param>
/// <param name="Output">Tool output (success: tool's textual response; failure: error message).</param>
/// <param name="IsError">True when policy denied the call OR the tool returned an error result.</param>
/// <param name="StartedUtc">When the invocation started (UTC).</param>
/// <param name="FinishedUtc">When the invocation finished or was denied (UTC).</param>
/// <param name="SessionId">Remote-session correlator, when the call ran inside a member×workspace session.</param>
/// <param name="OutputChars">Length of the full tool output (what <paramref name="Output"/> holds); null on legacy rows.</param>
/// <param name="ShapedOutputChars">Length of what the model actually received after tool-output shaping (equal to
/// <paramref name="OutputChars"/> when nothing was shaped); null on legacy rows.</param>
/// <param name="MaxOutputTokens">The <c>Tools:MaxOutputTokens</c> budget the result was shaped against; null when the
/// caller was not model-facing (no shaping) and on legacy rows.</param>
public sealed record ToolInvocationEvidence(
    string CallId,
    string ToolName,
    string TenantId,
    string? RunId,
    string Input,
    string Output,
    bool IsError,
    System.DateTimeOffset StartedUtc,
    System.DateTimeOffset FinishedUtc,
    string? SessionId = null,
    int? OutputChars = null,
    int? ShapedOutputChars = null,
    int? MaxOutputTokens = null)
{
    /// <summary>Wall-clock duration of the invocation.</summary>
    public System.TimeSpan Duration => FinishedUtc - StartedUtc;

    /// <summary>Characters shaping kept away from the model (0 when unshaped or unknown).</summary>
    public int SavedChars => OutputChars is { } full && ShapedOutputChars is { } shaped ? System.Math.Max(0, full - shaped) : 0;

    /// <summary>Where the side effect ran: null = on the server (through <see cref="IToolGateway"/>), or
    /// <see cref="ToolEvidenceOrigins.MemberComputer"/> for a tool the CLI on a member's paired computer ran itself.</summary>
    public string? Origin { get; init; }

    /// <summary>The agent role that made the call (Coding, Reviewer, Testing…), when known.</summary>
    public string? AgentRole { get; init; }

    /// <summary>The paired runner (computer) that ran it, for <see cref="ToolEvidenceOrigins.MemberComputer"/> rows.</summary>
    public string? RunnerId { get; init; }

    /// <summary>Who made the allow/deny decision: null = the server's <see cref="IToolPolicy"/>;
    /// <see cref="ToolEvidencePolicies.CliScopedPermissions"/> = the CLI's own scoped permissions on the member's computer.</summary>
    public string? PolicySource { get; init; }

    /// <summary>True when the policy refused the call (it never ran). A denial is also an error (<see cref="IsError"/>).</summary>
    public bool Denied { get; init; }

    /// <summary>A one-line, redacted view of <see cref="Input"/> for lists: a member's-computer row's recorded summary,
    /// else the input itself, redacted and cut to 160 characters. Only a <see cref="ToolEvidenceOrigins.MemberComputer"/>
    /// row is read as the recorded shape — a server tool whose input happens to carry a "summary" field is shown as-is.
    /// Computed on each read (JSON + redaction): a list computes it once per row, not per render.</summary>
    public string InputSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Input)) { return string.Empty; }
            return IsMemberComputer && MemberComputerInput.TryRead(Input, out var summary, out _)
                ? SecretRedactor.Summary(summary)
                : SecretRedactor.Summary(Input, 160);
        }
    }

    /// <summary>For a <see cref="ToolEvidenceOrigins.MemberComputer"/> row: what the SERVER asked the CLI to be allowed
    /// for this run (null = answer only, "scoped"…). <see cref="PolicySource"/> is what the computer itself reported.</summary>
    public string? RequestedAutonomy =>
        IsMemberComputer && MemberComputerInput.TryRead(Input, out _, out var requested) ? requested : null;

    private bool IsMemberComputer => string.Equals(Origin, ToolEvidenceOrigins.MemberComputer, System.StringComparison.Ordinal);
}

/// <summary>The <see cref="ToolInvocationEvidence.Input"/> of a tool call made on a member's computer:
/// <c>{"summary": "…", "requestedAutonomy": "scoped" | null}</c>.</summary>
public static class MemberComputerInput
{
    /// <summary>The stored input for a call's (already redacted) <paramref name="summary"/> and the autonomy the server
    /// requested for its run.</summary>
    public static string Write(string summary, string? requestedAutonomy)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("summary", summary);
            if (requestedAutonomy is null) { json.WriteNull("requestedAutonomy"); }
            else { json.WriteString("requestedAutonomy", requestedAutonomy); }
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Read a stored input back; false when it is not that shape.</summary>
    public static bool TryRead(string? input, out string summary, out string? requestedAutonomy)
    {
        summary = string.Empty;
        requestedAutonomy = null;
        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith('{')) { return false; }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(input);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("summary", out var s) || s.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return false;
            }
            summary = s.GetString() ?? string.Empty;
            requestedAutonomy = root.TryGetProperty("requestedAutonomy", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String
                ? r.GetString()
                : null;
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

/// <summary>Values of <see cref="ToolInvocationEvidence.Origin"/>.</summary>
public static class ToolEvidenceOrigins
{
    /// <summary>The CLI on a member's paired computer ran the tool (Bash, Edit, Write, Read…), outside the server gateway.</summary>
    public const string MemberComputer = "member-computer";
}

/// <summary>Values of <see cref="ToolInvocationEvidence.PolicySource"/>.</summary>
public static class ToolEvidencePolicies
{
    /// <summary>Allowed or denied by the CLI's scoped permissions on the member's computer (the runner's allow/deny lists).</summary>
    public const string CliScopedPermissions = "cli-scoped-permissions";

    /// <summary>The runner let the CLI skip its permission checks (<c>REMOTE_AGENT_YOLO</c>): every tool was allowed.</summary>
    public const string CliSkipPermissions = "cli-skip-permissions";

    /// <summary>The runner's operator set the CLI's flags themselves (<c>REMOTE_AGENT_ARGS</c>).</summary>
    public const string CliCustomArgs = "cli-custom-args";

    /// <summary>The CLI's default permissions (an answer-only run: any tool needing approval is refused).</summary>
    public const string CliDefaultPermissions = "cli-default-permissions";
}

/// <summary>Narrows <see cref="IToolInvocationLog.ListRecentAsync(string, ToolEvidenceFilter, int, System.Threading.CancellationToken)"/>.</summary>
/// <param name="Origin">Only rows from this origin (<see cref="ToolEvidenceOrigins"/>); null = every origin.</param>
/// <param name="SessionId">Only rows of this session (a Board task); null = every session.</param>
public sealed record ToolEvidenceFilter(string? Origin = null, string? SessionId = null)
{
    /// <summary>True when <paramref name="entry"/> passes the filter.</summary>
    public bool Matches(ToolInvocationEvidence entry)
    {
        System.ArgumentNullException.ThrowIfNull(entry);
        return (Origin is null || string.Equals(entry.Origin, Origin, System.StringComparison.Ordinal))
            && (SessionId is null || string.Equals(entry.SessionId, SessionId, System.StringComparison.Ordinal));
    }
}

/// <summary>Tool calls made on members' computers, per agent role (Insights).</summary>
/// <param name="AgentRole">The role ("" when the run didn't name one).</param>
/// <param name="Runs">Distinct runs that made at least one call.</param>
/// <param name="ToolCalls">Every recorded call, denials included.</param>
/// <param name="Denials">Calls the CLI's scoped permissions refused.</param>
public sealed record ToolCallsByRole(string AgentRole, int Runs, int ToolCalls, int Denials)
{
    /// <summary>Average calls per run.</summary>
    public double CallsPerRun => Runs == 0 ? 0 : (double)ToolCalls / Runs;

    /// <summary>Average denials per run.</summary>
    public double DenialsPerRun => Runs == 0 ? 0 : (double)Denials / Runs;

    /// <summary>Fold <paramref name="rows"/> (any origin) into per-role totals for <paramref name="origin"/> since
    /// <paramref name="sinceUtc"/>, one pass, ordered by calls (most first).</summary>
    public static System.Collections.Generic.IReadOnlyList<ToolCallsByRole> Fold(
        System.Collections.Generic.IEnumerable<ToolInvocationEvidence> rows, string origin, System.DateTimeOffset? sinceUtc)
    {
        System.ArgumentNullException.ThrowIfNull(rows);
        var byRole = new System.Collections.Generic.Dictionary<string, (System.Collections.Generic.HashSet<string> Runs, int Calls, int Denials)>(System.StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!string.Equals(r.Origin, origin, System.StringComparison.Ordinal) || (sinceUtc is { } since && r.StartedUtc < since)) { continue; }
            var role = r.AgentRole ?? string.Empty;
            if (!byRole.TryGetValue(role, out var acc)) { acc = (new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal), 0, 0); }
            acc.Runs.Add(r.RunId ?? r.CallId);
            byRole[role] = (acc.Runs, acc.Calls + 1, acc.Denials + (r.Denied ? 1 : 0));
        }
        return [.. byRole.Select(kv => new ToolCallsByRole(kv.Key, kv.Value.Runs.Count, kv.Value.Calls, kv.Value.Denials))
            .OrderByDescending(x => x.ToolCalls).ThenBy(x => x.AgentRole, System.StringComparer.Ordinal)];
    }
}

/// <summary>Writes <see cref="ToolInvocationEvidence"/> entries. Impls must be thread-safe.</summary>
public interface IToolInvocationLog
{
    /// <summary>Append one evidence entry. Best-effort: failures must NOT bubble up to the caller.</summary>
    System.Threading.Tasks.Task AppendAsync(
        ToolInvocationEvidence entry,
        System.Threading.CancellationToken cancellationToken = default);

    /// <summary>Snapshot of recently appended entries for the given tenant. Used by audit UIs and tests.</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(
        string tenantId,
        int limit = 50,
        System.Threading.CancellationToken cancellationToken = default);

    /// <summary>Recent entries for the tenant that pass <paramref name="filter"/> (e.g. only those made on a member's
    /// computer, or one task's). The default reads a wider recent window and filters it; durable sinks query directly.</summary>
    async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(
        string tenantId,
        ToolEvidenceFilter filter,
        int limit = 50,
        System.Threading.CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(filter);
        var rows = await ListRecentAsync(tenantId, System.Math.Max(limit, 1) * 10, cancellationToken).ConfigureAwait(false);
        return [.. rows.Where(filter.Matches).Take(System.Math.Max(limit, 1))];
    }

    /// <summary>Tool calls made on members' computers since <paramref name="sinceUtc"/> (null = all time), per agent
    /// role: runs, calls, denials (Insights). The default folds the recent window; durable sinks aggregate in the store.</summary>
    async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ToolCallsByRole>> SummarizeMemberComputerAsync(
        string tenantId,
        System.DateTimeOffset? sinceUtc,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var rows = await ListRecentAsync(tenantId, new ToolEvidenceFilter(ToolEvidenceOrigins.MemberComputer), 5000, cancellationToken).ConfigureAwait(false);
        return ToolCallsByRole.Fold(rows, ToolEvidenceOrigins.MemberComputer, sinceUtc);
    }
}
