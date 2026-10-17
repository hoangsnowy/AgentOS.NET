// Decorator around IOrchestratorAgent: generates a RunId, sets the MetricsContext (so each per-call
// RunMetric carries the RunId), runs the pipeline, then saves the PipelineResult + metrics to the DB.
// Persistence is best-effort — a DB error must not corrupt the result of a successful run.
using System.Collections.Concurrent;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Pipeline;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Pipeline.Orchestration;

internal sealed class PersistingOrchestratorAgent : IOrchestratorAgent
{
    private readonly IOrchestratorAgent _inner;
    private readonly IPipelineRunRepository _repository;
    private readonly IBudgetGuard _budgetGuard;
    private readonly TimeProvider _clock;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<PersistingOrchestratorAgent> _logger;
    private readonly IRunAdmission? _admission;

    public PersistingOrchestratorAgent(
        IOrchestratorAgent inner,
        IPipelineRunRepository repository,
        IBudgetGuard budgetGuard,
        TimeProvider clock,
        ITenantContext tenantContext,
        ILogger<PersistingOrchestratorAgent> logger,
        IRunAdmission? admission = null)
    {
        _admission = admission;
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _budgetGuard = budgetGuard ?? throw new ArgumentNullException(nameof(budgetGuard));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PipelineResult> RunAsync(UserStory story, CancellationToken cancellationToken = default)
    {
        // Run-level budget gate: protect the whole (expensive) pipeline run with one spend check.
        // One shared precedence (ambient → request ITenantContext → default) via AmbientIdentity.Resolve —
        // never a hardcoded `default`, which would bill a low-budget tenant's run against `default`.
        var tenantId = AmbientIdentity.Resolve(explicitTenantId: null, explicitUserId: null, _tenantContext).TenantId;
        // Fail-open on a transient budget-store error: a store hiccup must NOT crash the whole (expensive)
        // pipeline run — the cap re-applies on the next run once the store recovers. Mirrors IssueWorkAgent so
        // the two server-token entrypoints behave identically on the same fault.
        BudgetStatus budget;
        try
        {
            budget = await _budgetGuard.EvaluateAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) { budget = BudgetEvalFailed(ex); }
        catch (System.Data.Common.DbException ex) { budget = BudgetEvalFailed(ex); }
        catch (TimeoutException ex) { budget = BudgetEvalFailed(ex); }
        catch (System.IO.IOException ex) { budget = BudgetEvalFailed(ex); }
        catch (InvalidOperationException ex) { budget = BudgetEvalFailed(ex); }

        BudgetStatus BudgetEvalFailed(Exception ex)
        {
            _logger.LogWarning(ex, "Pipeline budget evaluation failed for tenant {Tenant}; proceeding.", tenantId);
            return BudgetStatus.Unset;
        }

        if (budget.IsBlocking)
        {
            throw new BudgetExceededException(tenantId, budget.CapUsd, budget.SpentUsd);
        }
        if (budget.State == BudgetState.Warn)
        {
            _logger.LogWarning(
                "LLM budget warning for tenant {Tenant}: spent ${Spent} of ${Cap} ({Percent:P0}) this month.",
                tenantId, budget.SpentUsd, budget.CapUsd, budget.Percent);
        }

        // E0.6: per-tenant admission (concurrency + burst) — refused runs never reach an agent.
        using var lease = _admission is null ? null : (await _admission.AdmitAsync(tenantId, cancellationToken).ConfigureAwait(false)).EnsureAdmitted();

        var runId = Guid.NewGuid();
        var createdAtUtc = _clock.GetUtcNow();

        // Per-run sink: each agent (LlmAgentBase) writes its RunMetric here as well as to the shared
        // bounded singleton. We persist from THIS sink — not from IMetricsCollector.Snapshot() — so a run's
        // own rows can't be evicted by other tenants' concurrent traffic before we read them back (which
        // would undercount run_metrics and let the BudgetGuard under-meter month-to-date spend). Mirrors
        // GraphExecutor's per-run GraphState.Spend bag.
        var runSink = new ConcurrentBag<RunMetric>();

        PipelineResult result;
        using (MetricsContext.BeginScope(runId.ToString(), "ad-hoc", sink: runSink))
        {
            result = await _inner.RunAsync(story, cancellationToken).ConfigureAwait(false);
        }

        var completedAtUtc = _clock.GetUtcNow();
        await PersistAsync(runId, result, runSink, createdAtUtc, completedAtUtc, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task PersistAsync(
        Guid runId,
        PipelineResult result,
        ConcurrentBag<RunMetric> runSink,
        DateTimeOffset createdAtUtc,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var runMetrics = runSink.ToList();

            await _repository.SaveAsync(
                new PipelineRunRecord(runId, result, runMetrics, createdAtUtc, completedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            OnPersistFailed(runId, ex);
        }
        catch (System.Data.Common.DbException ex)
        {
            OnPersistFailed(runId, ex);
        }
        catch (TimeoutException ex)
        {
            OnPersistFailed(runId, ex);
        }
        catch (IOException ex)
        {
            OnPersistFailed(runId, ex);
        }
        catch (InvalidOperationException ex)
        {
            OnPersistFailed(runId, ex);
        }

        // Persist best-effort: a DB error must not corrupt a successful run — log and move on.
        void OnPersistFailed(Guid id, Exception ex) =>
            _logger.LogError(ex, "Failed to save pipeline run {RunId} — the result is still returned to the client.", id);
    }
}
