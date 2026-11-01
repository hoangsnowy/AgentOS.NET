// Epic E5 — Evidence sink. Every tool invocation writes one entry so the run's audit trail
// covers not just the LLM prompts but every side-effect the model triggered. The default impl
// is in-memory ring buffer (visible via /tools/invocations); production wires an EF-backed
// repository that streams into the tools schema.

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
}
