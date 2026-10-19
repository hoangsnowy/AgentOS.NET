// The scheduler: every 30 s it claims due schedules (IScheduledRunStore.ClaimDueAsync — atomic per occurrence, so
// the Api and the Web, or several replicas, never double-run) and runs each through IOrchestratorAgent under the
// schedule owner's identity. That is the same path as POST /pipeline, so the budget gate, per-tenant run admission,
// persistence (Insights / Home recent runs) and metrics all apply with no special case. A run that the budget or
// the run limits refuse is recorded as skipped, never retried in a loop. At most two scheduled runs execute at once
// per host; the rest wait for the next tick (their occurrence is already claimed and stays owned by this host).

using AgentOs.Domain.Cost;
using AgentOs.Domain.Pipeline;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Pipeline.Scheduling;

internal sealed class ScheduledRunWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ScheduledRunWorker> logger) : BackgroundService
{
    /// <summary>How often due schedules are looked up.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private const int MaxConcurrentRuns = 2;
    private const int ClaimBatch = 10;
    private readonly SemaphoreSlim _slots = new(MaxConcurrentRuns, MaxConcurrentRuns);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, clock);
        do
        {
            try
            {
                await RunDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A scheduler tick must never take the host down (DB blip, migration in progress).
                logger.LogWarning(ex, "Scheduled-run tick failed; retrying next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One tick: claim what is due and start it (bounded). Returns the started runs (awaitable in tests).</summary>
    internal async Task<IReadOnlyList<Task>> RunDueAsync(CancellationToken ct)
    {
        IReadOnlyList<DueRun> due;
        using (var scope = scopes.CreateScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<IScheduledRunStore>()
                .ClaimDueAsync(clock.GetUtcNow(), ClaimBatch, ct).ConfigureAwait(false);
        }
        var started = new List<Task>(due.Count);
        foreach (var run in due)
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            started.Add(Task.Run(async () =>
            {
                try { await ExecuteOneAsync(run, ct).ConfigureAwait(false); }
                finally { _slots.Release(); }
            }, ct));
        }
        return started;
    }

    private async Task ExecuteOneAsync(DueRun due, CancellationToken ct)
    {
        string status;
        using (AmbientIdentity.Push(due.TenantId, due.Run.CreatedBy))
        using (var scope = scopes.CreateScope())
        {
            try
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<IOrchestratorAgent>();
                var result = await orchestrator.RunAsync(new UserStory(due.Run.Story), ct).ConfigureAwait(false);
                status = result.Status.ToString();
            }
            catch (BudgetExceededException)
            {
                status = "Skipped — budget cap reached";
            }
            catch (RunAdmissionDeniedException)
            {
                status = "Skipped — run limit reached";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Scheduled run {ScheduleId} failed.", due.Run.Id);
                status = "Failed — " + (ex.Message.Length > 160 ? ex.Message[..160] + "…" : ex.Message);
            }
        }
        using var outcome = scopes.CreateScope();
        await outcome.ServiceProvider.GetRequiredService<IScheduledRunStore>()
            .RecordOutcomeAsync(due.TenantId, due.Run.Id, clock.GetUtcNow(), status, ct).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _slots.Dispose();
        base.Dispose();
    }
}
