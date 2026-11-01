// S1 — the per-tenant budget gate, shared by every Board agent. A call that runs on the server spends AgentOS.NET's own
// LLM tokens, so it passes the tenant's ENFORCED monthly cap first. A call that really runs on a member's paired
// computer (the RemoteAgent provider — the member's own CLI subscription) costs 0 server tokens and is exempt. The
// decision reads the EFFECTIVE provider — the client the factory returned, after Settings' force-provider — never the
// name the agent asked for: a forced server provider must not slip past the gate because the profile says RemoteAgent.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Llm;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>The budget gate for agent calls, decided from the effective provider.</summary>
public static class AgentBudgetGate
{
    /// <summary>What a person reads when a call was blocked.</summary>
    public const string BlockedMessage =
        "Monthly LLM budget exceeded — raise the cap in Settings, or run this agent on a member's computer (0 server tokens).";

    /// <summary>True when <paramref name="provider"/> runs the call on a member's paired computer (0 server tokens).</summary>
    public static bool RunsOnMembersComputer(string? provider) =>
        (provider ?? string.Empty).Trim().ToUpperInvariant() is "REMOTEAGENT" or "REMOTE" or "IDE";

    /// <summary>The provider that will really serve calls on <paramref name="client"/> (after force-provider); the
    /// requested name only when the client does not say.</summary>
    public static string EffectiveProvider(ILlmClient client, string requested)
    {
        ArgumentNullException.ThrowIfNull(client);
        return string.IsNullOrWhiteSpace(client.Provider) ? requested : client.Provider;
    }

    /// <summary>
    /// The tenant's budget status when a call on <paramref name="effectiveProvider"/> must be blocked, else null (the call
    /// proceeds). No guard, a call on a member's computer, or an unknown tenant → null. Fail-open on a transient store
    /// error: a budget-store hiccup never blocks an agent; the cap applies again on the next call.
    /// </summary>
    public static async Task<BudgetStatus?> BlockedAsync(IBudgetGuard? guard, string? tenantId, string effectiveProvider,
        ILogger logger, string agent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (guard is null || RunsOnMembersComputer(effectiveProvider)) { return null; }
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? AmbientIdentity.Current?.TenantId : tenantId;
        if (string.IsNullOrWhiteSpace(tenant)) { return null; }

        BudgetStatus budget;
        try
        {
            budget = await guard.EvaluateAsync(tenant, ct).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) { return Failed(ex); }
        catch (System.Data.Common.DbException ex) { return Failed(ex); }
        catch (TimeoutException ex) { return Failed(ex); }
        catch (IOException ex) { return Failed(ex); }
        catch (InvalidOperationException ex) { return Failed(ex); }

        if (budget is not { State: BudgetState.Exceeded, EnforceOn: true }) { return null; }
        Metrics.PipelineTelemetry.RecordBudgetBlocked(Metrics.PipelineTelemetry.BudgetScopes.Agent, agent);
        logger.LogWarning("{Agent}: blocked — tenant {Tenant} is over its monthly LLM budget (${Spent} of ${Cap}) and the call would run on {Provider}.",
            agent, tenant, budget.SpentUsd, budget.CapUsd, effectiveProvider);
        return budget;

        BudgetStatus? Failed(Exception ex)
        {
            logger.LogWarning(ex, "{Agent}: budget evaluation failed for tenant {Tenant}; proceeding.", agent, tenant);
            return null;
        }
    }
}
