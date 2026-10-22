// Template Method base for the four LLM specialist agents (Requirement, Coding, Testing, QA). It owns
// the call skeleton every agent repeated verbatim: resolve the (possibly overridden) system prompt →
// build the LlmRequest from the agent's options → SendAsync → extract the JSON payload → optional schema
// validation → deserialize the DTO → DTO self-validation → record the run metric (success or failure) →
// log → map to the domain artifact. Parsing failures flow as a Result<TDto> rather than control-flow by
// exception; the boundary still throws LlmException so the public agent contract is unchanged.
//
// ExecuteAsync is a generic *method* (not a generic class) so each agent keeps its DTO types private and
// stays a plain public class — only the DTO-specific steps (validate / map / log) are passed as delegates.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Prompts;
using AgentOs.Modules.Pipeline.Validation;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Shared LLM-call → parse → validate → metrics → map skeleton for the specialist agents.</summary>
public abstract class LlmAgentBase
{
    private readonly ILlmClientFactory _factory;
    private readonly AgentOptions _options;
    private readonly IAgentProfiles? _profiles;
    private readonly IMetricsCollector _collector;
    private readonly ILlmOutputValidator? _validator;
    private readonly IPromptOverrides? _prompts;

    /// <summary>Initializes the shared agent plumbing. <paramref name="validator"/> is required only when
    /// <see cref="SchemaName"/> is non-null. With <paramref name="profiles"/> the agent resolves its
    /// provider/model/temperature/max tokens per call (Settings → Agent profiles, no restart);
    /// <paramref name="options"/> is then only the fallback.</summary>
    protected LlmAgentBase(
        ILlmClientFactory factory,
        AgentOptions options,
        IMetricsCollector collector,
        ILogger logger,
        ILlmOutputValidator? validator = null,
        IPromptOverrides? prompts = null,
        IAgentProfiles? profiles = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _profiles = profiles;
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator;
        _prompts = prompts;
    }

    /// <summary>Metric / error-message label; defaults to the concrete agent's type name.</summary>
    protected string AgentName => GetType().Name;

    /// <summary>Logger for the concrete agent (its category is preserved by the injected typed logger).</summary>
    protected ILogger Logger { get; }

    /// <summary>Prompt-override key consulted on <see cref="IPromptOverrides"/> (e.g. "Coding").</summary>
    protected abstract string PromptKey { get; }

    /// <summary>The built-in system prompt used when no per-tenant override is configured.</summary>
    protected abstract string DefaultSystemPrompt { get; }

    /// <summary>Schema to validate the JSON payload against, or null to skip schema validation (QA).</summary>
    protected virtual string? SchemaName => null;

    /// <summary>Pulls the JSON payload out of the raw LLM text. Defaults to <see cref="JsonExtractor.ExtractJson"/>;
    /// QA overrides this to pass the content through untouched.</summary>
    protected virtual string ExtractPayload(string content) => JsonExtractor.ExtractJson(content, AgentName);

    /// <summary>The full agent turn: build the request, call the LLM, parse + validate, record metrics,
    /// log, and map to the domain artifact. The DTO-specific steps are supplied by the caller.</summary>
    protected async Task<TResult> ExecuteAsync<TDto, TResult>(
        string userPrompt,
        Action<TDto> validateDto,
        Func<TDto, AgentMetrics, TResult> map,
        Action<AgentMetrics, TDto> logSuccess,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(validateDto);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(logSuccess);

        var systemPrompt = _prompts is null
            ? DefaultSystemPrompt
            : await _prompts.ResolveAsync(PromptKey, DefaultSystemPrompt, ct).ConfigureAwait(false);
        // E1.6: the tenant's workspace rules (Settings → Prompts) apply to every agent.
        if (_prompts is not null && await _prompts.ResolveRulesAsync(ct).ConfigureAwait(false) is { } rules)
        {
            systemPrompt += WorkspaceRules.ToPromptBlock(rules);
        }
        // E0.7: the target repo's own agent instructions (AGENTS.md, CLAUDE.md, …), when the run has a repo.
        // Appended after the agent's prompt so its role + output contract stay first and authoritative.
        if (RunGuidance.Current is { Length: > 0 } guidance)
        {
            systemPrompt += guidance;
        }

        // Resolve the live profile per call (an edit in Settings applies to the next call) and pick the client
        // for its provider — never frozen at construction.
        var profile = _profiles is null
            ? _options
            : await _profiles.ResolveAsync(PromptKey, _options, ct).ConfigureAwait(false);
        var llm = _factory.Create(profile.Provider);

        var request = new LlmRequest(systemPrompt, userPrompt, profile.Model, profile.Temperature, profile.MaxTokens)
        {
            // Carry the fixed agent role out-of-band so providers (notably the offline one) can identify the
            // agent without parsing the system prompt — robust against per-tenant prompt overrides.
            AgentRole = PromptKey,
            Timeout = LlmRequest.AgentAnswerTimeout,
        };
        var response = await llm.SendAsync(request, ct).ConfigureAwait(false);

        var parsed = Parse(response, validateDto);
        var metric = RunMetricFactory.From(response, AgentName, parsed.IsSuccess, parsed.IsSuccess ? null : parsed.Error);
        _collector.Add(metric);                        // shared live cost-dashboard working set (bounded, drop-oldest — lossy is fine)
        MetricsContext.Current?.RunSink?.Add(metric);  // run-owned durable copy the orchestrator persists — never evicted
        if (parsed.IsFailure)
        {
            throw new LlmException(parsed.Error!, AgentName);
        }

        var metrics = MetricsMapper.From(response);
        logSuccess(metrics, parsed.Value);
        return map(parsed.Value, metrics);
    }

    private Result<TDto> Parse<TDto>(LlmResponse response, Action<TDto> validateDto)
    {
        try
        {
            var payload = ExtractPayload(response.Content);
            if (SchemaName is { } schema)
            {
                if (_validator is null)
                {
                    throw new LlmException($"{AgentName}: schema '{schema}' configured but no validator was provided.", AgentName);
                }

                _validator.Validate(payload, schema, AgentName);
            }

            var dto = JsonExtractor.Deserialize<TDto>(payload, AgentName);
            validateDto(dto);
            return Result.Success(dto);
        }
        catch (LlmException ex)
        {
            // Expected, recoverable failure (bad JSON, schema mismatch, DTO invariant) — surface as a
            // value so ExecuteAsync records the failure metric in one place before rethrowing.
            return Result.Failure<TDto>(ex.Message);
        }
    }
}
