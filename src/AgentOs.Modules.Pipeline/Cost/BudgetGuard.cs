// Evaluates a tenant's month-to-date LLM spend against its configured cap. Reads the cap + enforce flag
// from the per-tenant AppConfig KV store and the spend from the persisted run_metrics (via the cost
// summary). Cap unset / unparseable / <= 0 => BudgetStatus.Unset, so a missing cap (and the no-database
// standalone path, where spend is always 0) can never block a run.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Cost;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Persistence;

namespace AgentOs.Modules.Pipeline.Cost;

internal sealed class BudgetGuard : IBudgetGuard
{
    /// <summary>AppConfig key holding the monthly cap in USD (invariant decimal string).</summary>
    internal const string CapKey = "budget/monthly-cap-usd";

    /// <summary>AppConfig key holding the enforce flag ("true" hard-blocks over-cap runs).</summary>
    internal const string EnforceKey = "budget/enforce";

    /// <summary>Fraction of the cap at which the state flips to Warn.</summary>
    internal const double WarnThreshold = 0.80;

    private readonly IAppConfigStore _config;
    private readonly IPipelineRunRepository _runs;
    private readonly TimeProvider _clock;

    public BudgetGuard(IAppConfigStore config, IPipelineRunRepository runs, TimeProvider clock)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<BudgetStatus> EvaluateAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var capRaw = await _config.GetForTenantAsync(tenantId, CapKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(capRaw)
            || !decimal.TryParse(capRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var cap)
            || cap <= 0m)
        {
            return BudgetStatus.Unset;
        }

        var enforceRaw = await _config.GetForTenantAsync(tenantId, EnforceKey, cancellationToken).ConfigureAwait(false);
        var enforce = string.Equals(enforceRaw, "true", StringComparison.OrdinalIgnoreCase);

        var now = _clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        // SUM-only: the gate needs the headline spend, not the dashboard's agent/provider/model/day buckets.
        var spent = await _runs.GetSpendForTenantAsync(tenantId, monthStart, cancellationToken).ConfigureAwait(false);

        var remaining = cap - spent;
        var percent = (double)(spent / cap);
        var state = spent >= cap
            ? BudgetState.Exceeded
            : percent >= WarnThreshold ? BudgetState.Warn : BudgetState.Ok;

        var status = new BudgetStatus(cap, spent, remaining, percent, state, enforce);
        PipelineTelemetry.RecordBudget(tenantId, status); // E1.10: the budget-over-cap alert reads this gauge
        return status;
    }
}
