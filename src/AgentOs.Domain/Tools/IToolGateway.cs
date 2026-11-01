// M1 — Tool gateway: the single server-side seam that gates (IToolPolicy), invokes (ITool) and
// records (IToolInvocationLog) one tool call. Extracted so EVERY execution path — the in-process
// LLM tool loop AND the remote dev-runner path (where the agentic loop runs server-side but the
// side effect happens off-box) — funnels through the same policy + evidence enforcement. The
// remote runner must never perform a side effect that did not pass through here on the server
// (the trust boundary).

namespace AgentOs.Domain.Tools;

/// <summary>Outcome of a gated tool invocation.</summary>
/// <param name="Output">The FULL tool output, the error message, or the policy-deny reason — what data consumers use.</param>
/// <param name="IsError">True when the policy denied the call OR the tool returned an error.</param>
/// <param name="Denied">True only when a policy decision short-circuited the call before the tool ran.</param>
/// <param name="ModelOutput">
/// The shaped text for a model (2.7 tool-output shaping), set only by a model-facing gateway; null = not shaped.
/// Read it through <see cref="ForModel"/>.
/// </param>
public sealed record ToolGatewayResult(string Output, bool IsError, bool Denied = false, string? ModelOutput = null)
{
    /// <summary>What to hand the model: <see cref="ModelOutput"/> when the gateway shaped it, else <see cref="Output"/>.</summary>
    public string ForModel => ModelOutput ?? Output;
}

/// <summary>
/// Gates, invokes and records a single tool call. The one place policy + evidence are enforced,
/// so any caller (the LLM tool loop, the remote-session executor) gets identical governance.
/// </summary>
public interface IToolGateway
{
    /// <summary>Run <paramref name="request"/> against <paramref name="tool"/> through policy + evidence.</summary>
    System.Threading.Tasks.Task<ToolGatewayResult> InvokeAsync(
        ITool tool,
        ToolInvocationRequest request,
        System.Threading.CancellationToken cancellationToken = default);
}
