// M1 — Default IToolGateway. A pure composition of the Domain tool interfaces (no framework
// dependencies), so it lives in Domain and is reusable by both Modules.Llm (AIToolFunction) and
// the remote-session executor without a cross-module reference. Policy + log are optional: a null
// policy skips gating, a null log skips evidence — but a tool call is ALWAYS recorded when a log
// is present, including policy denials, tool errors and anything that goes wrong after the tool ran.
// Evidence writes are best-effort and never break the call.
// 2.7 — a MODEL-FACING gateway (shapeForModel: true — AIToolFunction, the LLM tool loop) also shapes each result
// for the model (ToolOutputShaper: build/test logs trimmed to errors + summary, everything capped at
// Tools:MaxOutputTokens, read per call for the call's tenant) into ToolGatewayResult.ModelOutput. Output always stays
// the full result, so data consumers (a workflow Tool node) are never fed a shaped copy. The evidence keeps the full
// output plus both sizes and the cap, so the savings are measurable.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;

namespace AgentOs.Domain.Tools;

/// <summary>Default <see cref="IToolGateway"/>: policy gate -> invoke -> (shape for the model) -> evidence.</summary>
public sealed class DefaultToolGateway : IToolGateway
{
    private readonly IToolPolicy? _policy;
    private readonly IToolInvocationLog? _log;
    private readonly ISettings? _settings;
    private readonly bool _shapeForModel;

    /// <summary>Creates a gateway; every dependency is optional so stateless hosts and tests degrade gracefully.</summary>
    /// <param name="policy">Optional policy gate (null = no gating).</param>
    /// <param name="log">Optional evidence sink (null = no evidence).</param>
    /// <param name="settings">Optional settings for <see cref="ToolOutputShaper.MaxOutputTokensKey"/>; null = the default cap.</param>
    /// <param name="shapeForModel">
    /// True when the caller hands the result to a model: <see cref="ToolGatewayResult.ModelOutput"/> then carries the
    /// shaped text. False (data consumers) = no shaping; <see cref="ToolGatewayResult.Output"/> is always the full result.
    /// </param>
    public DefaultToolGateway(
        IToolPolicy? policy = null, IToolInvocationLog? log = null, ISettings? settings = null, bool shapeForModel = false)
    {
        _policy = policy;
        _log = log;
        _settings = settings;
        _shapeForModel = shapeForModel;
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

        // The cap is read BEFORE the tool runs: once the side effect has happened nothing (a cancelled token, a
        // settings-store failure) may stand between the result and its evidence row.
        int? maxTokens = _shapeForModel
            ? await ReadMaxOutputTokensAsync(request.TenantId, cancellationToken).ConfigureAwait(false)
            : null;

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

        string? modelOutput = null;
        try
        {
            if (maxTokens is { } cap)
            {
                var shaped = ShapeForModel(output, cap);
                modelOutput = shaped.Text;
                if (shaped.SavedChars > 0)
                {
                    ToolTelemetry.SavedChars.Add(
                        shaped.SavedChars, new KeyValuePair<string, object?>("kind", ToolTelemetry.KindOf(request.ToolName)));
                }
            }
        }
        finally
        {
            // The tool ran: its evidence row is written whatever happened above (full output + what the model got).
            await TryAppendAsync(request, output, result.IsError, started, modelOutput?.Length, maxTokens).ConfigureAwait(false);
        }
        return new ToolGatewayResult(output, result.IsError, ModelOutput: modelOutput);
    }

    // The shaper is a pure function over the text, but the tool already ran: whatever input trips it must not cost
    // the call or its evidence. Fall back to the plain size cap.
    private static ShapedToolOutput ShapeForModel(string output, int maxTokens)
    {
        try
        {
            return ToolOutputShaper.Shape(output, maxTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolOutputShaper.CapOnly(output, maxTokens);
        }
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
        // A settings-store hiccup (DB down, a missing key, the store's own timeout) must not fail a tool call: fall
        // back to the default cap. Only the caller's own cancellation propagates (the tool has not run yet).
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ToolOutputShaper.DefaultMaxOutputTokens;
        }
    }

    private async Task TryAppendAsync(
        ToolInvocationRequest request, string output, bool isError, DateTimeOffset started,
        int? shapedChars = null, int? maxOutputTokens = null)
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
                ShapedOutputChars: shapedChars ?? output.Length,
                MaxOutputTokens: maxOutputTokens)).ConfigureAwait(false);
        }
        // Evidence is best-effort — a log failure must never break the tool call (the EF sink swallows its own DB
        // errors; this covers any other sink). Cancellation is not swallowed.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex.Message;
        }
    }
}
