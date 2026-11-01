// M1 — Default IToolGateway. A pure composition of the Domain tool interfaces (no framework
// dependencies), so it lives in Domain and is reusable by both Modules.Llm (AIToolFunction) and
// the remote-session executor without a cross-module reference. Policy + log are optional: a null
// policy skips gating, a null log skips evidence — but a tool call is ALWAYS recorded when a log
// is present, including policy denials and tool errors. Evidence writes are best-effort and never
// break the call.
// 2.7 — the gateway is also where every tool result is SHAPED on its way back to the model (ToolOutputShaper:
// build/test logs trimmed to errors + summary, everything capped at Tools:MaxOutputTokens, read per call for the
// call's tenant). The evidence keeps the full output plus both sizes, so the savings are measurable.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;

namespace AgentOs.Domain.Tools;

/// <summary>Default <see cref="IToolGateway"/>: policy gate -> invoke -> shape -> evidence.</summary>
public sealed class DefaultToolGateway : IToolGateway
{
    private readonly IToolPolicy? _policy;
    private readonly IToolInvocationLog? _log;
    private readonly ISettings? _settings;

    /// <param name="policy">Optional policy gate (null = no gating).</param>
    /// <param name="log">Optional evidence sink (null = no evidence).</param>
    /// <param name="settings">Optional settings for <see cref="ToolOutputShaper.MaxOutputTokensKey"/>; null = the default cap.</param>
    public DefaultToolGateway(IToolPolicy? policy = null, IToolInvocationLog? log = null, ISettings? settings = null)
    {
        _policy = policy;
        _log = log;
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<ToolGatewayResult> InvokeAsync(
        ITool tool,
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(request);

        var started = DateTimeOffset.UtcNow;

        // Policy gate. Denied calls are surfaced to the model as a tool_result error and recorded
        // in the evidence log so the audit trail covers refusals as well as successes.
        if (_policy is not null)
        {
            var decision = await _policy.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                var reason = decision.Reason ?? $"Tool '{request.ToolName}' denied by policy.";
                await TryAppendAsync(request, reason, isError: true, started).ConfigureAwait(false);
                return new ToolGatewayResult(reason, IsError: true, Denied: true);
            }
        }

        ToolInvocationResult result;
        try
        {
            result = await tool.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A tool that THROWS (instead of returning IsError) would otherwise bypass evidence entirely and
            // fault the agent's chat loop — a hole in the audit trail exactly when something went wrong. The
            // ITool contract says a failure should become a tool_result error: record it and surface it as one.
            // Genuine cancellation is filtered out and propagates.
            var thrownMsg = $"Tool '{request.ToolName}' threw: {ex.Message}";
            await TryAppendAsync(request, thrownMsg, isError: true, started).ConfigureAwait(false);
            return new ToolGatewayResult(thrownMsg, IsError: true);
        }

        var output = result.IsError
            ? (string.IsNullOrEmpty(result.ErrorMessage)
                ? $"Tool '{request.ToolName}' returned an error."
                : result.ErrorMessage!)
            : result.Output;

        // Shape what the model sees; the evidence below still records the full output (audit) + both sizes.
        var maxTokens = await ReadMaxOutputTokensAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        var shaped = ToolOutputShaper.Shape(output, maxTokens);
        if (shaped.SavedChars > 0)
        {
            ToolTelemetry.SavedChars.Add(shaped.SavedChars, new System.Collections.Generic.KeyValuePair<string, object?>("tool", request.ToolName));
        }

        await TryAppendAsync(request, output, result.IsError, started, shaped.ShapedChars).ConfigureAwait(false);
        return new ToolGatewayResult(shaped.Text, result.IsError);
    }

    private async ValueTask<int> ReadMaxOutputTokensAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            return ToolOutputShaper.DefaultMaxOutputTokens;
        }
        try
        {
            var tokens = await _settings.GetIntAsync(
                ToolOutputShaper.MaxOutputTokensKey, tenantId, ToolOutputShaper.DefaultMaxOutputTokens, cancellationToken).ConfigureAwait(false);
            return ToolOutputShaper.ClampTokens(tokens);
        }
        // A settings-store hiccup must not fail a tool call that already ran: fall back to the default cap.
        catch (InvalidOperationException) { return ToolOutputShaper.DefaultMaxOutputTokens; }
        catch (TimeoutException) { return ToolOutputShaper.DefaultMaxOutputTokens; }
    }

    private async Task TryAppendAsync(
        ToolInvocationRequest request, string output, bool isError, DateTimeOffset started, int? shapedChars = null)
    {
        if (_log is null)
        {
            return;
        }
        try
        {
            await _log.AppendAsync(new ToolInvocationEvidence(
                CallId: request.CallId,
                ToolName: request.ToolName,
                TenantId: request.TenantId,
                RunId: request.RunId,
                Input: request.Input,
                Output: output,
                IsError: isError,
                StartedUtc: started,
                FinishedUtc: DateTimeOffset.UtcNow,
                SessionId: request.SessionId,
                OutputChars: output.Length,
                ShapedOutputChars: shapedChars ?? output.Length)).ConfigureAwait(false);
        }
        // Evidence is best-effort — a log failure must never break the tool call. Narrow to the
        // failure shapes a log sink realistically raises (the EF sink swallows its own DB errors).
        catch (ObjectDisposedException ex) { _ = ex.Message; }
        catch (IOException ex) { _ = ex.Message; }
        catch (TimeoutException ex) { _ = ex.Message; }
        catch (InvalidOperationException ex) { _ = ex.Message; }
    }
}
