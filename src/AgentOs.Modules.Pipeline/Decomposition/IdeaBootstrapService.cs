// Bootstrap (slice 2) — the "brains" of the bootstrap: free-text idea -> RequirementSpec -> seed
// tickets -> right-sized tickets. Pure thinking, NO GitHub: it returns a preview the Web shows for
// human approval, then the Web does the actual writes (labels + issues) via the source provider.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;

namespace AgentOs.Modules.Pipeline.Decomposition;

/// <summary>A generated requirement spec plus the tickets decomposed from it — the wizard's preview.</summary>
/// <param name="Spec">The structured requirement the idea produced.</param>
/// <param name="Tickets">The right-sized tickets to create (still editable before the user confirms).</param>
public sealed record BootstrapPreview(RequirementSpec Spec, IReadOnlyList<TicketDraft> Tickets);

/// <summary>Turns a free-text idea into a reviewable spec + ticket set (no side effects).</summary>
public interface IIdeaBootstrapService
{
    /// <summary>Run Requirement -> deterministic map -> LLM right-size and return the preview.</summary>
    Task<BootstrapPreview> GenerateAsync(string idea, string locale = "en-US", CancellationToken cancellationToken = default);
}

/// <inheritdoc />
internal sealed class IdeaBootstrapService : IIdeaBootstrapService
{
    private readonly IRequirementAgent _requirement;
    private readonly ITicketDecomposerAgent _decomposer;

    public IdeaBootstrapService(IRequirementAgent requirement, ITicketDecomposerAgent decomposer)
    {
        _requirement = requirement ?? throw new ArgumentNullException(nameof(requirement));
        _decomposer = decomposer ?? throw new ArgumentNullException(nameof(decomposer));
    }

    /// <inheritdoc />
    public async Task<BootstrapPreview> GenerateAsync(string idea, string locale = "en-US", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idea);

        var story = new UserStory(idea, NMax: 1, Locale: string.IsNullOrWhiteSpace(locale) ? "en-US" : locale);
        var spec = await _requirement.RunAsync(story, cancellationToken).ConfigureAwait(false);

        var seed = TicketMapper.Map(spec);
        var tickets = await _decomposer.RunAsync(spec, seed, idea, cancellationToken).ConfigureAwait(false);

        return new BootstrapPreview(spec, tickets);
    }
}
