// Bootstrap (slice 2) — the LLM "right-size" pass over the deterministic seed. A lean agent (no QA
// loop, no schema validator): it sends the spec + seed, parses a tickets array, and keeps only labels
// from the standard taxonomy. On ANY failure (LLM error, unparseable JSON, empty result) it returns
// the deterministic seed unchanged, so the bootstrap flow never dies on a flaky model.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.Decomposition;

/// <summary>Refines a deterministic ticket seed into a right-sized, taxonomy-clean set via the LLM.</summary>
public interface ITicketDecomposerAgent
{
    /// <summary>Refine <paramref name="seed"/> against <paramref name="spec"/>; returns the seed on failure.</summary>
    Task<IReadOnlyList<TicketDraft>> RunAsync(RequirementSpec spec, IReadOnlyList<TicketDraft> seed, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class TicketDecomposerAgent : ITicketDecomposerAgent
{
    private const string AgentName = nameof(TicketDecomposerAgent);

    private static readonly HashSet<string> AllowedLabels =
        new(StandardLabels.All.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);

    private readonly ILlmClientFactory _factory;
    private readonly AgentOptions _options;
    private readonly ILogger<TicketDecomposerAgent> _logger;
    private readonly IAgentProfiles? _profiles;

    public TicketDecomposerAgent(
        ILlmClientFactory factory,
        IOptions<AgentsOptions> options,
        ILogger<TicketDecomposerAgent> logger,
        IAgentProfiles? profiles = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Decomposer;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _profiles = profiles;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TicketDraft>> RunAsync(RequirementSpec spec, IReadOnlyList<TicketDraft> seed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(seed);

        // Live profile (Settings → Agent profiles), resolved per call.
        var profile = _profiles is null
            ? _options
            : await _profiles.ResolveAsync(nameof(AgentsOptions.Decomposer), _options, cancellationToken).ConfigureAwait(false);
        var llm = _factory.Create(profile.Provider);
        var request = new LlmRequest(
            SystemPrompt: DecomposerPrompt.System,
            UserPrompt: DecomposerPrompt.RenderUser(spec, seed),
            Model: profile.Model,
            Temperature: profile.Temperature,
            MaxTokens: profile.MaxTokens);

        try
        {
            var response = await llm.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var dto = JsonExtractor.Deserialize<DecomposedDto>(response.Content, AgentName);

            var tickets = (dto.Tickets ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.Title))
                .Select(ToDraft)
                .ToList();

            if (tickets.Count == 0)
            {
                _logger.LogWarning("{Agent}: LLM returned no tickets; using the deterministic seed.", AgentName);
                return seed;
            }
            return tickets;
        }
        catch (LlmException ex) { return OnDecomposeFailed(ex); }
        catch (JsonException ex) { return OnDecomposeFailed(ex); }

        IReadOnlyList<TicketDraft> OnDecomposeFailed(Exception ex)
        {
            _logger.LogWarning(ex, "{Agent}: decomposition failed; using the deterministic seed.", AgentName);
            return seed;
        }
    }

    // Keep only taxonomy labels, then force the ai-gate label to agree with the aiReady flag.
    private static TicketDraft ToDraft(TicketDto dto)
    {
        var labels = (dto.Labels ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l) && AllowedLabels.Contains(l!))
            .Select(l => l!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var aiReady = dto.AiReady;
        labels.RemoveAll(l =>
            string.Equals(l, StandardLabels.AiReady, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(l, StandardLabels.NeedsHuman, StringComparison.OrdinalIgnoreCase));
        labels.Add(aiReady ? StandardLabels.AiReady : StandardLabels.NeedsHuman);

        return new TicketDraft(dto.Title!.Trim(), dto.Body?.Trim() ?? string.Empty, labels, aiReady);
    }

    private sealed class DecomposedDto
    {
        public List<TicketDto>? Tickets { get; set; }
    }

    private sealed class TicketDto
    {
        public string? Title { get; set; }
        public string? Body { get; set; }
        public List<string?>? Labels { get; set; }
        public bool AiReady { get; set; }
    }
}
