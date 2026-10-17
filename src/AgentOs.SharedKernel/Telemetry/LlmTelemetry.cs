// Cross-cutting OpenTelemetry instrumentation for LLM provider calls, following the GenAI semantic
// conventions (gen_ai.*). Lives in SharedKernel so every provider client (Modules.Llm + the RemoteAgent
// client) can emit through ONE ActivitySource + Meter named "AgentOs.Llm" with no cross-module reference.
// Register with .AddSource("AgentOs.Llm") / .AddMeter("AgentOs.Llm") (done in ServiceDefaults).

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentOs.SharedKernel.Telemetry;

/// <summary>Emits gen_ai.* spans + metrics around a single LLM call. Static, process-lifetime.</summary>
public static class LlmTelemetry
{
    /// <summary>The ActivitySource / Meter name. Register both under this name.</summary>
    public const string SourceName = "AgentOs.Llm";

    /// <summary>The shared LLM ActivitySource (also used by tests via an ActivityListener).</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    private static readonly Histogram<long> TokenUsage =
        Meter.CreateHistogram<long>("gen_ai.client.token.usage", unit: "{token}", description: "Tokens used per LLM call.");
    private static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("gen_ai.client.operation.duration", unit: "s", description: "LLM call duration.");
    private static readonly Histogram<double> Cost =
        Meter.CreateHistogram<double>("agentos.llm.cost.usd", unit: "USD", description: "LLM call cost in USD.");
    private static readonly Counter<long> Calls =
        Meter.CreateCounter<long>("agentos.llm.calls", unit: "{call}", description: "LLM calls made.");

    /// <summary>Maps an AgentOS.NET provider name to the OTel GenAI <c>gen_ai.system</c> well-known value.</summary>
    public static string SystemFor(string provider) => provider switch
    {
        "Claude" => "anthropic",
        "AzureOpenAI" or "MAF" => "azure.ai.openai",
        "RemoteAgent" => "agentos.remote_agent",
        _ => provider,
    };

    /// <summary>Starts a client span for one provider call. Dispose it when the call ends (use <c>using</c>).
    /// Returns null when no listener is active — callers must null-check.</summary>
    public static Activity? StartChat(string system, string requestModel, string? tenantId)
    {
        var activity = ActivitySource.StartActivity($"chat {requestModel}", ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("gen_ai.system", system);
        activity.SetTag("gen_ai.operation.name", "chat");
        activity.SetTag("gen_ai.request.model", requestModel);
        if (!string.IsNullOrEmpty(tenantId))
        {
            activity.SetTag("agentos.tenant.id", tenantId);
        }

        return activity;
    }

    /// <summary>Records a SUCCESSFUL call: span attributes + the four instruments. Call exactly once, on the
    /// success path only — a rate-limit failover attempt must use <see cref="RecordError"/> so retries never
    /// double-count usage.</summary>
    public static void RecordSuccess(
        Activity? activity,
        string system,
        string requestModel,
        string responseModel,
        int inputTokens,
        int outputTokens,
        decimal costUsd,
        double seconds)
    {
        var cost = (double)costUsd; // OTel attributes/instruments take double/long, never decimal.

        if (activity is not null)
        {
            activity.SetTag("gen_ai.response.model", responseModel);
            activity.SetTag("gen_ai.usage.input_tokens", inputTokens);
            activity.SetTag("gen_ai.usage.output_tokens", outputTokens);
            activity.SetTag("agentos.cost.usd", cost);
            activity.SetStatus(ActivityStatusCode.Ok);
        }

        var systemTag = new KeyValuePair<string, object?>("gen_ai.system", system);
        var modelTag = new KeyValuePair<string, object?>("gen_ai.request.model", requestModel);

        TokenUsage.Record(inputTokens, systemTag, modelTag, new KeyValuePair<string, object?>("gen_ai.token.type", "input"));
        TokenUsage.Record(outputTokens, systemTag, modelTag, new KeyValuePair<string, object?>("gen_ai.token.type", "output"));
        OperationDuration.Record(seconds, systemTag, modelTag);
        Cost.Record(cost, systemTag, modelTag);
        Calls.Add(1, systemTag, modelTag);
    }

    /// <summary>Flags the span as failed (e.g. a rate-limited failover attempt). Emits no usage metrics.</summary>
    public static void RecordError(Activity? activity, string message)
        => activity?.SetStatus(ActivityStatusCode.Error, message);
}
