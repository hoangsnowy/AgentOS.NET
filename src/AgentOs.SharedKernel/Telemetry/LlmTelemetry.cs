// Cross-cutting OpenTelemetry instrumentation for LLM provider calls, following the GenAI semantic
// conventions (gen_ai.*). Lives in SharedKernel so every provider client (Modules.Llm + the RemoteAgent
// client) can emit through ONE ActivitySource + Meter named "AgentOs.Llm" with no cross-module reference.
// Register with .AddSource("AgentOs.Llm") / .AddMeter("AgentOs.Llm") (done in ServiceDefaults).

using System;
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

    // Bucket advice (E1.10): the SDK default buckets (0…10 000) are millisecond-shaped, so a p95 over seconds-long
    // LLM calls or cents-sized costs would be meaningless. Boundaries follow the GenAI semconv advice where it exists.
    private static readonly Histogram<long> TokenUsage =
        Meter.CreateHistogram<long>("gen_ai.client.token.usage", unit: "{token}", description: "Tokens used per LLM call.",
            tags: null, advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = [1, 4, 16, 64, 256, 1024, 4096, 16384, 65536, 262144, 1048576, 4194304] });
    private static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("gen_ai.client.operation.duration", unit: "s", description: "LLM call duration.",
            tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92, 163.84, 327.68, 655.36] });
    // Unit "{USD}" (a UCUM annotation, as OTel recommends for non-UCUM units): a bare "USD" is appended to the
    // Prometheus name as "_USD" (agentos_llm_cost_usd_USD_sum); the annotation is dropped → agentos_llm_cost_usd_sum.
    private static readonly Histogram<double> Cost =
        Meter.CreateHistogram<double>("agentos.llm.cost.usd", unit: "{USD}", description: "LLM call cost in USD.",
            tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0, 0.0001, 0.0005, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10] });
    private static readonly Counter<long> Calls =
        Meter.CreateCounter<long>("agentos.llm.calls", unit: "{call}", description: "LLM calls made.");
    private static readonly Counter<long> Tokens =
        Meter.CreateCounter<long>("agentos.llm.tokens", unit: "{token}",
            description: "LLM tokens per billing class (agentos.token.class: fresh_input, cache_read, cache_write, output; reasoning is a subset of output).");
    private static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("agentos.llm.errors", unit: "{error}",
            description: "Failed LLM call attempts by error.type (rate_limited, transient, auth, bad_request, other).");

    /// <summary>Instrument names, for tests and the operations docs (docs/OPERATIONS.md).</summary>
    public static class Instruments
    {
        /// <summary>Histogram of tokens per call (gen_ai semconv; input includes cache reads + writes).</summary>
        public const string TokenUsage = "gen_ai.client.token.usage";
        /// <summary>Histogram of call duration in seconds (gen_ai semconv).</summary>
        public const string OperationDuration = "gen_ai.client.operation.duration";
        /// <summary>Histogram of call cost in USD.</summary>
        public const string CostUsd = "agentos.llm.cost.usd";
        /// <summary>Counter of successful calls.</summary>
        public const string Calls = "agentos.llm.calls";
        /// <summary>Counter of tokens per billing class.</summary>
        public const string Tokens = "agentos.llm.tokens";
        /// <summary>Counter of failed call attempts.</summary>
        public const string Errors = "agentos.llm.errors";
    }

    /// <summary>Well-known <c>error.type</c> values on <see cref="Instruments.Errors"/>.</summary>
    public static class ErrorTypes
    {
        /// <summary>HTTP 429 / "rate limit" / "overloaded" — the key pool fails over.</summary>
        public const string RateLimited = "rate_limited";
        /// <summary>5xx, timeout or transport failure.</summary>
        public const string Transient = "transient";
        /// <summary>401/403 — a bad or revoked key.</summary>
        public const string Auth = "auth";
        /// <summary>400/404/409/422 — the request itself is wrong.</summary>
        public const string BadRequest = "bad_request";
        /// <summary>Anything unclassified.</summary>
        public const string Other = "other";
    }

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

    /// <summary>Records a SUCCESSFUL call: span attributes + the instruments. Call exactly once, on the
    /// success path only — a rate-limit failover attempt must use <see cref="RecordError(Activity?, string)"/> so
    /// retries never double-count usage. Token counts follow the MEAI convention: <paramref name="cacheReadTokens"/>
    /// and <paramref name="cacheWriteTokens"/> are subsets of <paramref name="inputTokens"/>,
    /// <paramref name="reasoningTokens"/> a subset of <paramref name="outputTokens"/>. <paramref name="agent"/> (the
    /// agent role, e.g. "Coding") tags every instrument as <c>gen_ai.agent.name</c> when known.</summary>
    public static void RecordSuccess(
        Activity? activity,
        string system,
        string requestModel,
        string responseModel,
        int inputTokens,
        int outputTokens,
        decimal costUsd,
        double seconds,
        int cacheReadTokens = 0,
        int cacheWriteTokens = 0,
        int reasoningTokens = 0,
        string? agent = null)
    {
        var cost = (double)costUsd; // OTel attributes/instruments take double/long, never decimal.

        if (activity is not null)
        {
            activity.SetTag("gen_ai.response.model", responseModel);
            activity.SetTag("gen_ai.usage.input_tokens", inputTokens);
            activity.SetTag("gen_ai.usage.output_tokens", outputTokens);
            activity.SetTag("agentos.cost.usd", cost);
            if (cacheReadTokens > 0) { activity.SetTag("gen_ai.usage.cache_read.input_tokens", cacheReadTokens); }
            if (cacheWriteTokens > 0) { activity.SetTag("gen_ai.usage.cache_creation.input_tokens", cacheWriteTokens); }
            if (!string.IsNullOrWhiteSpace(agent)) { activity.SetTag("gen_ai.agent.name", agent); }
            activity.SetStatus(ActivityStatusCode.Ok);
        }

        var tags = new TagList
        {
            { "gen_ai.system", system },
            { "gen_ai.request.model", requestModel },
        };
        if (!string.IsNullOrWhiteSpace(agent))
        {
            tags.Add("gen_ai.agent.name", agent);
        }

        TokenUsage.Record(inputTokens, With(tags, "gen_ai.token.type", "input"));
        TokenUsage.Record(outputTokens, With(tags, "gen_ai.token.type", "output"));
        OperationDuration.Record(seconds, tags);
        Cost.Record(cost, tags);
        Calls.Add(1, tags);

        // Billing classes (owner directive 3): fresh input · cache read · cache write · output (· reasoning ⊂ output).
        // Clamped so a provider's odd report can never produce a negative fresh-input count.
        var cacheRead = Math.Clamp(cacheReadTokens, 0, Math.Max(inputTokens, 0));
        var cacheWrite = Math.Clamp(cacheWriteTokens, 0, Math.Max(inputTokens - cacheRead, 0));
        Tokens.Add(Math.Max(inputTokens - cacheRead - cacheWrite, 0), With(tags, TokenClassTag, "fresh_input"));
        Tokens.Add(cacheRead, With(tags, TokenClassTag, "cache_read"));
        Tokens.Add(cacheWrite, With(tags, TokenClassTag, "cache_write"));
        Tokens.Add(Math.Max(outputTokens, 0), With(tags, TokenClassTag, "output"));
        if (reasoningTokens > 0)
        {
            Tokens.Add(Math.Min(reasoningTokens, Math.Max(outputTokens, 0)), With(tags, TokenClassTag, "reasoning"));
        }
    }

    /// <summary>The tag naming a token's billing class on <see cref="Instruments.Tokens"/>.</summary>
    public const string TokenClassTag = "agentos.token.class";

    /// <summary>Flags the span as failed (e.g. a rate-limited failover attempt). Emits no usage metrics.</summary>
    public static void RecordError(Activity? activity, string message)
        => activity?.SetStatus(ActivityStatusCode.Error, message);

    /// <summary>Flags the span as failed AND counts the attempt on <see cref="Instruments.Errors"/> under
    /// <paramref name="errorType"/> (one of <see cref="ErrorTypes"/>) — the signal behind the error-rate panel and
    /// the 429-storm alert. Emits no usage metrics.</summary>
    public static void RecordError(Activity? activity, string message, string system, string requestModel, string errorType, string? agent = null)
    {
        activity?.SetTag("error.type", errorType);
        RecordError(activity, message);
        var tags = new TagList
        {
            { "gen_ai.system", system },
            { "gen_ai.request.model", requestModel },
            { "error.type", errorType },
        };
        if (!string.IsNullOrWhiteSpace(agent))
        {
            tags.Add("gen_ai.agent.name", agent);
        }
        Errors.Add(1, tags);
    }

    private static TagList With(TagList tags, string key, string value)
    {
        var copy = tags; // TagList is a struct — this is a copy, the shared base list is never mutated.
        copy.Add(key, value);
        return copy;
    }
}
