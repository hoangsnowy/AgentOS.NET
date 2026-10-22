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
    /// <summary>Refine <paramref name="seed"/> against <paramref name="spec"/>; returns the seed on failure. <paramref name="idea"/>
    /// is the person's own text: tickets it names ("#12") can become dependencies of the new ones.</summary>
    Task<IReadOnlyList<TicketDraft>> RunAsync(RequirementSpec spec, IReadOnlyList<TicketDraft> seed, string? idea = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed partial class TicketDecomposerAgent : ITicketDecomposerAgent
{
    private const string AgentName = nameof(TicketDecomposerAgent);

    private static readonly HashSet<string> AllowedLabels =
        new(StandardLabels.All.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);

    private readonly ILlmClientFactory _factory;
    private readonly AgentOptions _options;
    private readonly ILogger<TicketDecomposerAgent> _logger;
    private readonly IAgentProfiles? _profiles;
    private readonly AgentOs.Modules.Pipeline.Prompts.IPromptOverrides? _prompts;

    public TicketDecomposerAgent(
        ILlmClientFactory factory,
        IOptions<AgentsOptions> options,
        ILogger<TicketDecomposerAgent> logger,
        IAgentProfiles? profiles = null,
        AgentOs.Modules.Pipeline.Prompts.IPromptOverrides? prompts = null)
    {
        _prompts = prompts;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Decomposer;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _profiles = profiles;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TicketDraft>> RunAsync(RequirementSpec spec, IReadOnlyList<TicketDraft> seed, string? idea = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(seed);

        // Live profile (Settings → Agent profiles), resolved per call.
        var profile = _profiles is null
            ? _options
            : await _profiles.ResolveAsync(nameof(AgentsOptions.Decomposer), _options, cancellationToken).ConfigureAwait(false);
        var llm = _factory.Create(profile.Provider);
        // E1.6: the tenant may override this prompt and its workspace rules apply here too.
        var system = _prompts is null
            ? DecomposerPrompt.System
            : await _prompts.ResolveAsync("Decomposer", DecomposerPrompt.System, cancellationToken).ConfigureAwait(false)
              + AgentOs.Modules.Pipeline.Prompts.WorkspaceRules.ToPromptBlock(await _prompts.ResolveRulesAsync(cancellationToken).ConfigureAwait(false));
        var request = new LlmRequest(
            SystemPrompt: system,
            UserPrompt: DecomposerPrompt.RenderUser(spec, seed, idea),
            Model: profile.Model,
            Temperature: profile.Temperature,
            MaxTokens: profile.MaxTokens,
            Timeout: LlmRequest.AgentAnswerTimeout);

        try
        {
            var response = await llm.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var dto = JsonExtractor.Deserialize<DecomposedDto>(response.Content, AgentName);

            // Dependencies point at indices of the LLM's list; dropping a blank ticket shifts them, so re-map.
            var all = dto.Tickets ?? [];
            var kept = all.Select((t, i) => (t, i)).Where(x => !string.IsNullOrWhiteSpace(x.t.Title)).ToList();
            var newIndex = kept.Select((x, j) => (x.i, j)).ToDictionary(p => p.i, p => p.j);
            // Existing tickets only as the idea names them — a number the model made up never becomes a dependency.
            var named = IssueNumbersIn(idea);
            var tickets = kept
                .Select(x => ToDraft(x.t) with
                {
                    DependsOn = (x.t.DependsOn ?? []).Where(newIndex.ContainsKey).Select(d => newIndex[d])
                        .Where(d => d != newIndex[x.i]).Distinct().ToList(),
                    DependsOnIssues = (x.t.DependsOnIssues ?? []).Where(named.Contains).Distinct().Order().ToList(),
                })
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

    /// <summary>The ticket numbers a text names as "#N".</summary>
    internal static IReadOnlySet<int> IssueNumbersIn(string? text) =>
        string.IsNullOrEmpty(text)
            ? new HashSet<int>()
            : IssueRef().Matches(text)
                .Select(m => int.TryParse(m.Groups[1].ValueSpan, global::System.Globalization.NumberStyles.None, global::System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0)
                .Where(n => n > 0).ToHashSet();

    [global::System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w&])#(\d{1,7})\b")]
    private static partial global::System.Text.RegularExpressions.Regex IssueRef();

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
        public List<int>? DependsOn { get; set; }
        public List<int>? DependsOnIssues { get; set; }
    }
}
