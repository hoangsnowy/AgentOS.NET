// SDK-based provider client: a keyed pool of Microsoft.Extensions.AI IChatClient instances (one per
// API key) selected by ApiKeyRouter, with round-robin + rate-limit (429) failover. Provider-agnostic
// — the key->IChatClient factory and the "is this a rate-limit error" predicate are injected, so the
// same class serves Claude (Anthropic.SDK) and Azure OpenAI (Azure.AI.OpenAI).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Tools;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Logging;
using AgentOs.SharedKernel.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Llm;

/// <summary><see cref="ILlmClient"/> backed by a pool of <see cref="IChatClient"/> instances keyed by API key.</summary>
public sealed class PooledChatLlmClient : ILlmClient, IDisposable
{
    private readonly Func<string, string, IChatClient> _clientFactory;
    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<string>>> _keyProvider;
    private readonly ApiKeyRouter _router;
    private readonly Func<Exception, bool> _isRateLimited;
    private readonly Func<Exception, LlmErrorKind> _classify;
    private readonly Func<Exception, TimeSpan?> _retryAfter;
    private readonly ILogger _logger;
    private readonly TimeSpan _baseDelay;
    private readonly IToolRegistry? _toolRegistry;
    private readonly ITenantContext? _tenantContext;
    // Root provider for resolving the request-scoped ITenantContext PER CALL. This client is a keyed
    // SINGLETON, so it must NOT capture a scoped ITenantContext at construction (that throws
    // "scoped from root" under scope validation when the host's tenant context is scoped, e.g. Keycloak's
    // HttpTenantContext). Used only as a fallback when no AmbientIdentity is on the call.
    private readonly IServiceProvider? _tenantProvider;
    private readonly IToolPolicy? _toolPolicy;
    private readonly IToolInvocationLog? _toolInvocationLog;
    // Optional extra cache-key dimension resolved per call (e.g. the tenant's Azure endpoint). Without it, two
    // tenants that resolve the SAME key but DIFFERENT endpoints would share one cached IChatClient bound to
    // whichever endpoint won the GetOrAdd race — sending tenant B's request to tenant A's resource.
    private readonly Func<string>? _cacheKeyDiscriminator;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IChatClient> _wrappedClients = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Provider { get; }

    public PooledChatLlmClient(
        string provider,
        Func<string, string, IChatClient> clientFactory,
        Func<CancellationToken, ValueTask<IReadOnlyList<string>>> keyProvider,
        ApiKeyRouter router,
        Func<Exception, bool> isRateLimited,
        Func<Exception, TimeSpan?> retryAfter,
        ILogger logger,
        TimeSpan? baseDelay = null,
        IToolRegistry? toolRegistry = null,
        ITenantContext? tenantContext = null,
        IToolPolicy? toolPolicy = null,
        IToolInvocationLog? toolInvocationLog = null,
        IServiceProvider? tenantProvider = null,
        Func<Exception, LlmErrorKind>? classifyError = null,
        Func<string>? cacheKeyDiscriminator = null)
    {
        Provider = string.IsNullOrWhiteSpace(provider) ? throw new ArgumentException("provider required", nameof(provider)) : provider;
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _isRateLimited = isRateLimited ?? throw new ArgumentNullException(nameof(isRateLimited));
        // Default classifier preserves the legacy behavior (only a rate-limit error fails over) so callers
        // that pass just isRateLimited are unchanged; production passes SdkChatClients.Classify for the full
        // transient/auth/bad-request classification.
        _classify = classifyError ?? (ex => _isRateLimited(ex) ? LlmErrorKind.Transient : LlmErrorKind.Other);
        _retryAfter = retryAfter ?? (_ => null);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _toolRegistry = toolRegistry;
        _tenantContext = tenantContext;
        _tenantProvider = tenantProvider;
        _toolPolicy = toolPolicy;
        _toolInvocationLog = toolInvocationLog;
        _cacheKeyDiscriminator = cacheKeyDiscriminator;
    }

    // The tenant for tool tagging / telemetry when there is no AmbientIdentity on the call. Prefer a
    // directly-supplied ITenantContext (tests); otherwise resolve the request-scoped one inside a fresh
    // scope off the root provider (safe from a singleton — IHttpContextAccessor is itself a singleton, so
    // the scoped HttpTenantContext still sees the current request). Returns null when neither is available.
    private string? ResolveFallbackTenant()
    {
        if (_tenantContext is not null) { return _tenantContext.TenantId; }
        if (_tenantProvider is null) { return null; }
        using var scope = _tenantProvider.CreateScope();
        return scope.ServiceProvider.GetService<ITenantContext>()?.TenantId;
    }

    /// <inheritdoc />
    public async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var keys = await _keyProvider(cancellationToken).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            throw new LlmException(
                $"No {Provider} API key configured for your workspace. Open Settings → API keys and add your " +
                $"{Provider} key (or ask a workspace admin). Keys are stored per-tenant and used only by your workspace.",
                Provider);
        }

        var messages = new List<ChatMessage>(2);
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, request.SystemPrompt));
        }
        messages.Add(new ChatMessage(ChatRole.User, request.UserPrompt));
        var resolvedTools = ResolveTools(request.Tools);
        var options = new ChatOptions
        {
            ModelId = request.Model,
            Temperature = (float)request.Temperature,
            MaxOutputTokens = request.MaxTokens,
            Tools = resolvedTools.Count > 0 ? resolvedTools.Cast<AITool>().ToList() : null,
        };

        var maxAttempts = Math.Max(1, keys.Count);
        Exception? last = null;

        var tenantId = AmbientIdentity.Current?.TenantId ?? ResolveFallbackTenant();
        var genAiSystem = LlmTelemetry.SystemFor(Provider);
        // Resolve the extra cache dimension (e.g. the tenant's Azure endpoint) ONCE per call — the same value
        // feeds the cache key AND the client factory, so a cached client can never be bound to a stale endpoint.
        var cacheDiscriminator = _cacheKeyDiscriminator?.Invoke() ?? string.Empty;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = _router.Acquire(Provider, keys)!;
            var clientCacheKey = $"{key}|{cacheDiscriminator}|{request.Model}";
            var chat = _clients.GetOrAdd(clientCacheKey, _ => _clientFactory(key, request.Model));
            if (resolvedTools.Count > 0)
            {
                // FunctionInvokingChatClient wrapper drives the tool-call loop transparently so the
                // ILlmClient.SendAsync contract still returns one LlmResponse — the final text turn.
                chat = _wrappedClients.GetOrAdd(clientCacheKey, _ => chat.AsBuilder().UseFunctionInvocation().Build());
            }

            // One span per attempt; usage/metrics fire only on the success return below, so a 429
            // failover attempt (handled in the catch) never double-counts tokens or cost.
            using var activity = LlmTelemetry.StartChat(genAiSystem, request.Model, tenantId);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await chat.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                var inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
                var outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);
                var cost = CostCalculator.Calculate(request.Model, inputTokens, outputTokens);
                if (!CostCalculator.IsKnown(request.Model))
                {
                    // Unpriced model = cost is a pessimistic ceiling estimate until the model is added to the table.
                    _logger.LogWarning(
                        "Cost: model {Model} is not in the price table — cost ESTIMATED at the fail-closed ceiling rate so it still counts against the budget. Add it to CostCalculator.",
                        LogSafe.Scrub(request.Model));
                }
                LlmTelemetry.RecordSuccess(activity, genAiSystem, request.Model, response.ModelId ?? request.Model,
                    inputTokens, outputTokens, cost, stopwatch.Elapsed.TotalSeconds);
                _logger.LogInformation(
                    "LlmCallCompleted {Provider} {Model} {InTok} {OutTok} {CostUsd} {Ms} {Tenant}",
                    Provider, LogSafe.Scrub(request.Model), inputTokens, outputTokens, cost, stopwatch.Elapsed.TotalMilliseconds, LogSafe.Scrub(tenantId ?? ""));
                return new LlmResponse(
                    Content: response.Text ?? string.Empty,
                    InputTokens: inputTokens,
                    OutputTokens: outputTokens,
                    CostUsd: cost,
                    Latency: stopwatch.Elapsed,
                    Model: request.Model,
                    Provider: Provider);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Deliberately broad, gated by the injected classifier: providers' SDKs surface a failure as
            // different exception types (ClientResultException, HttpRequestException, provider-specific), so
            // the CLASSIFIER — not the catch type — decides failover. We retry the next key on a transient
            // error (429 / 5xx / timeout) or an auth error (a different key may be valid); a bad-request or
            // unclassified error is non-retryable and propagates. (CodeQL cs/catch-of-all-exceptions is
            // expected to flag this single line.)
            catch (Exception ex) when (ShouldFailover(ex)) { await HandleFailover(ex).ConfigureAwait(false); }

            async Task HandleFailover(Exception ex)
            {
                LlmTelemetry.RecordError(activity, ex.Message);
                last = ex;
                var kind = _classify(ex);
                // Penalize the key: a transient error honors the server's Retry-After; an auth failure has no
                // cooldown signal (the key is simply wrong) so it gets the default cooldown.
                _router.Penalize(Provider, key, kind == LlmErrorKind.Transient ? _retryAfter(ex) : null);
                _logger.LogWarning("[{Provider}] {Kind} on a key; {Available}/{Total} keys available — failing over.",
                    Provider, kind, _router.AvailableCount(Provider, keys), keys.Count);

                if (attempt + 1 < maxAttempts)
                {
                    var wait = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new LlmException($"{Provider} failed on all {keys.Count} key(s) after {maxAttempts} attempt(s) (last: {last?.Message}).", Provider, innerException: last);
    }

    // A key-pool retry is warranted only for a transient (429 / 5xx / timeout) or auth (wrong key — another
    // key may be valid) failure. A bad-request or unclassified error is non-retryable and propagates.
    private bool ShouldFailover(Exception ex)
    {
        var kind = _classify(ex);
        return kind is LlmErrorKind.Transient or LlmErrorKind.Auth;
    }

    private List<AIToolFunction> ResolveTools(IReadOnlyList<string>? requested)
    {
        var resolved = new List<AIToolFunction>();
        if (requested is null || requested.Count == 0 || _toolRegistry is null)
        {
            return resolved;
        }

        var tenantId = AgentOs.SharedKernel.Identity.AmbientIdentity.Current?.TenantId ?? ResolveFallbackTenant() ?? "anonymous";
        foreach (var name in requested)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var tool = _toolRegistry.Resolve(name);
            if (tool is null)
            {
                _logger.LogWarning("[{Provider}] tool '{Tool}' requested but not registered — dropped.", Provider, name);
                continue;
            }
            resolved.Add(new AIToolFunction(tool, tenantId, runId: null, policy: _toolPolicy, log: _toolInvocationLog));
        }
        return resolved;
    }

    /// <summary>Disposes the pooled chat clients (this is a keyed singleton, so the DI container
    /// disposes it on shutdown). Each base client is disposed exactly once — a FunctionInvoking
    /// wrapper cascades to its inner base client, so base clients that have a wrapper are skipped.</summary>
    public void Dispose()
    {
        // Snapshot the wrapped-key set first: a concurrent in-flight SendAsync could otherwise add a wrapper for
        // a base client between the ContainsKey check and the Dispose call below, so the base client would be
        // disposed both directly here AND via its freshly-added wrapper — breaking the "exactly once" guarantee.
        var wrappedKeys = new HashSet<string>(_wrappedClients.Keys, StringComparer.Ordinal);

        foreach (var wrapped in _wrappedClients.Values)
        {
            wrapped.Dispose();
        }

        foreach (var (cacheKey, client) in _clients)
        {
            if (!wrappedKeys.Contains(cacheKey))
            {
                client.Dispose();
            }
        }

        _wrappedClients.Clear();
        _clients.Clear();
    }
}
