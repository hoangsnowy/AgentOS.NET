// AgentOs.Infrastructure/Orchestration/PipelineOrchestrator.cs
// Phase 4 — Implements IOrchestratorAgent. Runs the 4 specialists + QA loop up to NMax iterations.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Pipeline;
using AgentOs.Domain;
using AgentOs.Domain.Code;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Qa;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Testing;
using AgentOs.SharedKernel.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.Orchestration;

/// <summary>
/// Orchestrates the end-to-end pipeline flow:
/// <list type="number">
///   <item>RequirementAgent(story) → spec</item>
///   <item>Loop ≤ NMax: CodingAgent → TestingAgent → QaAgent → check IsConsistent</item>
///   <item>Return a PipelineResult with the QA history + total metrics</item>
/// </list>
/// </summary>
public sealed class PipelineOrchestrator : IOrchestratorAgent
{
    private readonly IRequirementAgent _requirement;
    private readonly ICodingAgent _coding;
    private readonly ITestingAgent _testing;
    private readonly IQaAgent _qa;
    private readonly PipelineOptions _options;
    private readonly AgentOs.Modules.Pipeline.Agents.IAgentProfiles? _profiles;
    private readonly ILogger<PipelineOrchestrator> _logger;
    private readonly IPipelineProgressSink _progress;

    /// <summary>Initializes.</summary>
    /// <param name="requirement">Requirement Agent (KC1).</param>
    /// <param name="coding">Coding Agent (KC2).</param>
    /// <param name="testing">Testing Agent (KC3).</param>
    /// <param name="qa">QA Agent (KC5).</param>
    /// <param name="options">Pipeline configuration.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="progress">
    /// Realtime progress sink — an optional parameter so existing call-sites are not broken.
    /// <c>null</c> ⇒ use <see cref="NullPipelineProgressSink"/> (no-op).
    /// </param>
    public PipelineOrchestrator(
        IRequirementAgent requirement,
        ICodingAgent coding,
        ITestingAgent testing,
        IQaAgent qa,
        IOptions<PipelineOptions> options,
        ILogger<PipelineOrchestrator> logger,
        IPipelineProgressSink? progress = null,
        AgentOs.Modules.Pipeline.Agents.IAgentProfiles? profiles = null)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        _requirement = requirement ?? throw new System.ArgumentNullException(nameof(requirement));
        _coding = coding ?? throw new System.ArgumentNullException(nameof(coding));
        _testing = testing ?? throw new System.ArgumentNullException(nameof(testing));
        _qa = qa ?? throw new System.ArgumentNullException(nameof(qa));
        _options = options.Value;
        _profiles = profiles;
        _logger = logger ?? throw new System.ArgumentNullException(nameof(logger));
        _progress = progress ?? NullPipelineProgressSink.Instance;
    }

    /// <inheritdoc />
    public async Task<PipelineResult> RunAsync(UserStory story, CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(story);
        story.Validate();

        // Clamp to >= 1: a non-positive NMax or MaxIterations would skip the loop entirely, leaving
        // Code/Tests null behind the non-nullable PipelineResult contract (NRE in any consumer).
        // Live QA-loop cap (Settings → Pipeline), resolved per run.
        var cap = _profiles is null
            ? _options.MaxIterations
            : await _profiles.MaxIterationsAsync(_options.MaxIterations, cancellationToken).ConfigureAwait(false);
        var maxIterations = System.Math.Max(1, System.Math.Min(story.NMax, cap));
        _logger.LogInformation(
            "Pipeline start: story={Title}, maxIter={Max}",
            LogSafe.Scrub(Truncate(story.Description, 60)), maxIterations);

        // KC1 — Requirement Agent.
        RequirementSpec spec;
        await ReportStartAsync(PipelineStage.Requirement, 0, maxIterations,
            "Analyze user story → requirement spec", cancellationToken).ConfigureAwait(false);
        try
        {
            spec = await _requirement.RunAsync(story, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmException ex)
        {
            _logger.LogError(ex, "RequirementAgent failed — pipeline abort.");
            await ReportFailedAsync(PipelineStage.Requirement, 0, maxIterations, ex.Message, cancellationToken).ConfigureAwait(false);
            return FailEarly(story, ex);
        }

        await ReportDoneAsync(PipelineStage.Requirement, 0, maxIterations,
            $"Spec: {spec.Title} ({spec.FunctionalRequirements.Count} FR, {spec.Entities.Count} entities)",
            spec.Metrics, cancellationToken).ConfigureAwait(false);

        var qaHistory = new List<QaReport>(maxIterations);
        CodeArtifact? code = null;
        TestArtifact? tests = null;
        QaReport? lastQa = null;
        var status = PipelineStatus.MaxIterationReached;

        for (var iter = 1; iter <= maxIterations; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Iteration {N}/{Max} start", iter, maxIterations);

            var stage = PipelineStage.Coding;
            try
            {
                // KC2 — Coding Agent.
                await ReportStartAsync(PipelineStage.Coding, iter, maxIterations,
                    iter == 1 ? "Generate source code from spec" : "Regenerate code based on QA feedback", cancellationToken).ConfigureAwait(false);
                code = await _coding.RunAsync(spec, lastQa, cancellationToken).ConfigureAwait(false);
                await ReportDoneAsync(PipelineStage.Coding, iter, maxIterations,
                    $"{code.Files.Count} files ({code.Architecture})", code.Metrics, cancellationToken).ConfigureAwait(false);

                // KC3 — Testing Agent.
                stage = PipelineStage.Testing;
                await ReportStartAsync(PipelineStage.Testing, iter, maxIterations,
                    "Generate test cases (happy / edge / error)", cancellationToken).ConfigureAwait(false);
                tests = await _testing.RunAsync(spec, code, lastQa, cancellationToken).ConfigureAwait(false);
                await ReportDoneAsync(PipelineStage.Testing, iter, maxIterations,
                    $"{tests.TotalCount} tests (cov ~{tests.EstimatedCoveragePercent}%)", tests.Metrics, cancellationToken).ConfigureAwait(false);

                // KC5 — QA Agent.
                stage = PipelineStage.Qa;
                await ReportStartAsync(PipelineStage.Qa, iter, maxIterations,
                    "Assess requirement-code-test consistency", cancellationToken).ConfigureAwait(false);
                lastQa = await _qa.RunAsync(spec, code, tests, cancellationToken).ConfigureAwait(false);
            }
            catch (LlmException ex)
            {
                _logger.LogError(ex, "Iteration {N} failed.", iter);
                await ReportFailedAsync(stage, iter, maxIterations, ex.Message, cancellationToken).ConfigureAwait(false);
                return FailMidway(story, spec, code, tests, qaHistory);
            }

            qaHistory.Add(lastQa);
            _logger.LogInformation(
                "Iteration {N} QA: score={Score} consistent={Consistent}",
                iter, lastQa.Score, lastQa.IsConsistent);

            await _progress.ReportAsync(
                new PipelineProgressEvent(
                    Stage: PipelineStage.Qa,
                    Phase: PipelinePhase.Completed,
                    Iteration: iter,
                    MaxIterations: maxIterations,
                    Message: lastQa.IsConsistent
                        ? $"QA pass (score {lastQa.Score:0.00}) — exit loop"
                        : $"QA not passing (score {lastQa.Score:0.00}, {lastQa.Issues.Count} issues)",
                    QaScore: lastQa.Score,
                    QaConsistent: lastQa.IsConsistent,
                    Metrics: lastQa.Metrics),
                cancellationToken).ConfigureAwait(false);

            if (lastQa.IsConsistent)
            {
                status = PipelineStatus.Done;
                break;
            }
        }

        var total = Aggregate(spec.Metrics, code?.Metrics, tests?.Metrics, qaHistory);

        await ReportDoneAsync(PipelineStage.Aggregate, qaHistory.Count, maxIterations,
            status == PipelineStatus.Done
                ? $"Completed after {qaHistory.Count} iterations — QA pass"
                : $"Reached the limit of {maxIterations} iterations — QA not passing",
            total, cancellationToken).ConfigureAwait(false);

        return new PipelineResult(
            UserStory: story,
            Spec: spec,
            Code: code!,
            Tests: tests!,
            QaHistory: qaHistory,
            Status: status,
            TotalMetrics: total);
    }

    private ValueTask ReportStartAsync(PipelineStage stage, int iteration, int maxIterations, string message, CancellationToken ct)
        => _progress.ReportAsync(
            new PipelineProgressEvent(stage, PipelinePhase.Started, iteration, maxIterations, message), ct);

    private ValueTask ReportDoneAsync(PipelineStage stage, int iteration, int maxIterations, string message, AgentMetrics metrics, CancellationToken ct)
        => _progress.ReportAsync(
            new PipelineProgressEvent(stage, PipelinePhase.Completed, iteration, maxIterations, message, Metrics: metrics), ct);

    private ValueTask ReportFailedAsync(PipelineStage stage, int iteration, int maxIterations, string message, CancellationToken ct)
        => _progress.ReportAsync(
            new PipelineProgressEvent(stage, PipelinePhase.Failed, iteration, maxIterations, message), ct);

    private static AgentMetrics Aggregate(AgentMetrics spec, AgentMetrics? code, AgentMetrics? tests, IReadOnlyList<QaReport> qaHistory)
    {
        var sum = spec;
        if (code is not null)
        {
            sum = sum.Add(code);
        }
        if (tests is not null)
        {
            sum = sum.Add(tests);
        }
        foreach (var q in qaHistory)
        {
            sum = sum.Add(q.Metrics);
        }
        return sum;
    }

    private static PipelineResult FailEarly(UserStory story, System.Exception ex)
        => new(
            UserStory: story,
            Spec: ErrorSpec(ex),
            Code: EmptyCode(),
            Tests: EmptyTests(),
            QaHistory: [],
            Status: PipelineStatus.Failed,
            TotalMetrics: AgentMetrics.Empty);

    // The exception is fully logged at the call site, so it is not carried into the result.
    private static PipelineResult FailMidway(
        UserStory story, RequirementSpec spec, CodeArtifact? code, TestArtifact? tests,
        IReadOnlyList<QaReport> history)
        => new(
            UserStory: story,
            Spec: spec,
            Code: code ?? EmptyCode(),
            Tests: tests ?? EmptyTests(),
            QaHistory: history,
            Status: PipelineStatus.Failed,
            TotalMetrics: AgentMetrics.Empty);

    private static RequirementSpec ErrorSpec(System.Exception ex)
        => new("(failed)", ex.Message, [], [], [], [], [], [], AgentMetrics.Empty);

    private static CodeArtifact EmptyCode()
        => new("(none)", "Clean Architecture", [], null, AgentMetrics.Empty);

    private static TestArtifact EmptyTests()
        => new("xUnit", [], 0, 0, 0, 0, AgentMetrics.Empty);

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}
