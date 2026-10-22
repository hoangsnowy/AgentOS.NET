// Reports each call to the open LlmCallScope (see AgentOs.Domain.Llm.LlmCallScope). The factory only wraps a client
// when a scope is open, so the pipeline's own metrics path and every unscoped call are untouched.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Llm;

/// <summary>An <see cref="ILlmClient"/> that records its calls into the scope that was open when it was created.</summary>
internal sealed class ObservedLlmClient(ILlmClient inner, LlmCallScope scope) : ILlmClient
{
    public string Provider => inner.Provider;

    public async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        try
        {
            var r = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
            scope.Add(new LlmCallRecord(request.AgentRole, r.Model, r.Provider, r.InputTokens, r.OutputTokens,
                r.CacheReadTokens, r.CacheWriteTokens, r.ReasoningTokens, r.CostUsd,
                r.Latency > TimeSpan.Zero ? r.Latency : watch.Elapsed, true, null, DateTimeOffset.UtcNow));
            return r;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Add(new LlmCallRecord(request.AgentRole, request.Model, inner.Provider, 0, 0, 0, 0, 0, 0m, watch.Elapsed,
                false, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message, DateTimeOffset.UtcNow));
            throw;
        }
    }
}
