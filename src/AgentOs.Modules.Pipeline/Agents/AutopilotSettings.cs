// How Autopilot runs a board (ADR-0011: UI settings, per tenant — Settings › Autopilot). Who decides the next moves
// (the Orchestrator agent by default; "Rules" keeps the fixed rules only, e.g. to save the member's subscription), how
// each ticket runs (a MAF workflow, or step by step), and the bounds every engine keeps: tasks at once, acceptance runs
// at once, automatic fix rounds, the Orchestrator agent's extra retries, and how long to wait for a pull request's
// checks. Read per pass / per call, so a change applies to the next pass without a restart.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>The bounds Autopilot keeps on a board, as configured (<see cref="AutopilotSettings"/>).</summary>
/// <param name="MaxParallel">Tasks running at once on a board.</param>
/// <param name="MaxParallelTests">Acceptance runs at once on a member's computer (each brings the app up on fixed ports).</param>
/// <param name="MaxFixRounds">Automatic "send it back to the Coding agent" rounds before a person is asked — the total
/// across engines and restarts (a task's persisted fix rounds count against it).</param>
/// <param name="MaxAgentRetries">Extra retries the Orchestrator agent may spend on a stuck task, with guidance.</param>
/// <param name="ChecksTimeout">How long a ticket's MAF workflow waits for a pull request's checks.</param>
/// <param name="ChecksPoll">How often it looks at them.</param>
public sealed record AutopilotLimits(int MaxParallel, int MaxParallelTests, int MaxFixRounds, int MaxAgentRetries, TimeSpan ChecksTimeout, TimeSpan ChecksPoll)
{
    /// <summary>The built-in values (the settings' defaults).</summary>
    public static AutopilotLimits Default { get; } = new(
        AutopilotSettings.DefaultMaxParallel, AutopilotSettings.DefaultMaxParallelTests, AutopilotSettings.DefaultMaxFixRounds,
        AutopilotSettings.DefaultMaxAgentRetries, TimeSpan.FromMinutes(AutopilotSettings.DefaultChecksTimeoutMinutes),
        TimeSpan.FromSeconds(AutopilotSettings.DefaultChecksPollSeconds));

    /// <summary>These limits with no slot caps — every legal move is offered (the Orchestrator agent picks, then
    /// the caps are applied to its picks).</summary>
    public AutopilotLimits Uncapped => this with { MaxParallel = int.MaxValue, MaxParallelTests = int.MaxValue };
}

/// <summary>Declares the <c>Autopilot:*</c> settings.</summary>
public sealed class AutopilotSettings : ISettingsContributor
{
    /// <summary>The settings category (and the Settings › Autopilot page).</summary>
    public const string Category = "Autopilot";

    /// <summary>The setting key.</summary>
    public const string DecidedByKey = "Autopilot:DecidedBy";

    /// <summary>The Orchestrator agent decides (within the rules).</summary>
    public const string Agent = "Orchestrator agent";

    /// <summary>The fixed rules decide.</summary>
    public const string Rules = "Rules";

    /// <summary>How each started ticket runs.</summary>
    public const string TicketEngineKey = "Autopilot:TicketEngine";

    /// <summary>A Microsoft Agent Framework workflow runs the ticket's whole agent flow.</summary>
    public const string MafWorkflow = "MAF workflow";

    /// <summary>Autopilot drives each step itself, pass by pass.</summary>
    public const string StepByStep = "Step by step";

    /// <summary>Tasks running at once on a board.</summary>
    public const string MaxParallelKey = "Autopilot:MaxParallel";

    /// <summary>Acceptance runs at once on a member's computer.</summary>
    public const string MaxParallelTestsKey = "Autopilot:MaxParallelTests";

    /// <summary>Automatic fix rounds per task.</summary>
    public const string MaxFixRoundsKey = "Autopilot:MaxFixRounds";

    /// <summary>The Orchestrator agent's extra retries per stuck task.</summary>
    public const string MaxAgentRetriesKey = "Autopilot:MaxAgentRetries";

    /// <summary>How long to wait for a pull request's checks (minutes).</summary>
    public const string ChecksTimeoutMinutesKey = "Autopilot:ChecksTimeoutMinutes";

    /// <summary>How often to look at them (seconds).</summary>
    public const string ChecksPollSecondsKey = "Autopilot:ChecksPollSeconds";

    /// <summary>The longest one Coding (tickets) run may take on a computer (minutes) — clone, change, build, test, push.</summary>
    public const string CodingRunMinutesKey = "Autopilot:CodingRunMinutes";

    /// <summary>The longest one Reviewer run may take (minutes).</summary>
    public const string ReviewRunMinutesKey = "Autopilot:ReviewRunMinutes";

    /// <summary>The longest one Testing (tickets) run may take (minutes) — bring the app up and check every criterion.</summary>
    public const string TestingRunMinutesKey = "Autopilot:TestingRunMinutes";

    /// <summary>Whether a Coding, Reviewer or Testing run that ended without its JSON reply is asked once for it.</summary>
    public const string RecoverMissingReplyKey = "Autopilot:RecoverMissingReply";

    internal const int DefaultMaxParallel = 2;
    internal const int DefaultMaxParallelTests = 1;
    internal const int DefaultMaxFixRounds = 3;
    internal const int DefaultMaxAgentRetries = 2;
    internal const int DefaultChecksTimeoutMinutes = 60;
    internal const int DefaultChecksPollSeconds = 60;
    internal const int DefaultCodingRunMinutes = 60;
    internal const int DefaultReviewRunMinutes = 30;
    internal const int DefaultTestingRunMinutes = 45;

    /// <summary>Each bound's key, range and default — the descriptors and the typed read share it.</summary>
    private static readonly (string Key, int Min, int Max, int Default)[] Bounds =
    [
        (MaxParallelKey, 1, 6, DefaultMaxParallel),
        (MaxParallelTestsKey, 1, 3, DefaultMaxParallelTests),
        (MaxFixRoundsKey, 0, 10, DefaultMaxFixRounds),
        (MaxAgentRetriesKey, 0, 5, DefaultMaxAgentRetries),
        (ChecksTimeoutMinutesKey, 5, 240, DefaultChecksTimeoutMinutes),
        (ChecksPollSecondsKey, 15, 600, DefaultChecksPollSeconds),
        (CodingRunMinutesKey, 10, 240, DefaultCodingRunMinutes),
        (ReviewRunMinutesKey, 5, 120, DefaultReviewRunMinutes),
        (TestingRunMinutesKey, 10, 180, DefaultTestingRunMinutes),
    ];

    /// <summary>True when the stored value means a MAF workflow (anything but "Step by step").</summary>
    public static bool UsesMafWorkflow(string? value) => !string.Equals(value?.Trim(), StepByStep, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return new(DecidedByKey, SettingKind.Choice, Category, "Decided by")
        {
            DefaultValue = Agent,
            Choices = [Agent, Rules],
            Help = "Orchestrator agent: an AI lead reads the whole project and picks the next moves, retries stuck tasks with guidance and asks you precise questions — always within the rules. Rules: the fixed rules alone.",
        };
        yield return new(TicketEngineKey, SettingKind.Choice, Category, "Each ticket runs as")
        {
            DefaultValue = MafWorkflow,
            Choices = [MafWorkflow, StepByStep],
            Help = "MAF workflow: a Microsoft Agent Framework graph runs the ticket flow (Workflow › Ticket flow — Code → Checks → Review → Test → Merge by default) for the ticket, routing every verdict. Step by step: Autopilot drives each step itself every minute. Both run the same ticket flow, agents, rules and evidence.",
        };
        yield return Bound(MaxParallelKey, "Tasks at once",
            "How many tickets the AI works on at the same time on a project (coding, fixing, reviewing or testing). Each one uses your computer.");
        yield return Bound(MaxParallelTestsKey, "Acceptance tests at once",
            "How many changes the Testing agent tries at the same time on one computer. Each brings the app up on its usual ports, so more than one only works when the app can run twice.");
        yield return Bound(MaxFixRoundsKey, "Automatic fix rounds",
            "How many times a change goes back to the Coding agent (red checks, a conflict, review or acceptance findings) before a person is asked. Counted per task, in total — a restart grants no new rounds.");
        yield return Bound(MaxAgentRetriesKey, "Orchestrator retries",
            "Extra tries the Orchestrator agent may give a stuck task, with its own guidance, after the automatic fix rounds are used up.");
        yield return Bound(ChecksTimeoutMinutesKey, "Wait for checks (minutes)",
            "How long a ticket's MAF workflow waits for the pull request's checks before handing it back to Autopilot's step-by-step passes.");
        yield return Bound(ChecksPollSecondsKey, "Look at checks every (seconds)",
            "How often the MAF workflow looks at the pull request's checks while it waits.");
        yield return Bound(CodingRunMinutesKey, "Coding run time limit (minutes)",
            "The longest one Coding agent run may take on your computer before it is stopped: clone, change, build, test and push. Big tickets (migrations, upgrades) need more.");
        yield return Bound(ReviewRunMinutesKey, "Review run time limit (minutes)",
            "The longest one Reviewer agent run may take.");
        yield return Bound(TestingRunMinutesKey, "Testing run time limit (minutes)",
            "The longest one Testing agent run may take: bring the app up and check every acceptance criterion.");
        yield return new(RecoverMissingReplyKey, SettingKind.Boolean, Category, "Ask again for a missing reply")
        {
            DefaultValue = "true",
            Help = "When a Coding, Reviewer or Testing run ends without its summary (it ran out of turns or context after doing the work), ask it once, briefly, for the summary instead of discarding the run. A Coding run's branch is checked on the remote before it counts.",
        };
    }

    /// <summary>Whether a run that ended without its reply is asked once for it (<see cref="RecoverMissingReplyKey"/>), read now.</summary>
    public static ValueTask<bool> ReadRecoverMissingReplyAsync(ISettings settings, string? tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.GetBoolAsync(RecoverMissingReplyKey, tenantId, true, ct);
    }

    /// <summary>A run time limit (<see cref="CodingRunMinutesKey"/>, …), read now and clamped to its range.</summary>
    public static async ValueTask<TimeSpan> ReadRunLimitAsync(ISettings settings, string? tenantId, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var b = Array.Find(Bounds, x => x.Key == key);
        if (b.Key is null) { throw new ArgumentException($"Not a run time limit: {key}", nameof(key)); }
        var minutes = await settings.GetIntAsync(key, tenantId, b.Default, ct).ConfigureAwait(false);
        return TimeSpan.FromMinutes(Math.Clamp(minutes, b.Min, b.Max));
    }

    /// <summary>The run time limit an agent applies: the tenant's value through <paramref name="profiles"/>, or the built-in
    /// one when there is no profile source or it gives nothing usable (zero or negative).</summary>
    public static async ValueTask<TimeSpan> RunLimitForAsync(IAgentProfiles? profiles, string key, CancellationToken ct = default)
    {
        var limit = profiles is null ? TimeSpan.Zero : await profiles.RunLimitAsync(key, ct).ConfigureAwait(false);
        return limit > TimeSpan.Zero ? limit : DefaultRunLimit(key);
    }

    /// <summary>A run time limit's built-in value.</summary>
    public static TimeSpan DefaultRunLimit(string key) => TimeSpan.FromMinutes(Array.Find(Bounds, x => x.Key == key).Default);

    private static SettingDescriptor Bound(string key, string label, string help)
    {
        var b = Array.Find(Bounds, x => x.Key == key);
        return new(key, SettingKind.WholeNumber, Category, label)
        {
            DefaultValue = b.Default.ToString(CultureInfo.InvariantCulture),
            Min = b.Min,
            Max = b.Max,
            Help = help,
        };
    }

    /// <summary>True when the stored value means the agent decides (anything but "Rules").</summary>
    public static bool AgentDecides(string? value) => !string.Equals(value?.Trim(), Rules, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tenant's Autopilot bounds, read now (each clamped to its range — a seeded value can't escape it).</summary>
    public static async ValueTask<AutopilotLimits> ReadLimitsAsync(ISettings settings, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var v = new int[Bounds.Length];
        for (var i = 0; i < Bounds.Length; i++)
        {
            var (key, min, max, fallback) = Bounds[i];
            v[i] = Math.Clamp(await settings.GetIntAsync(key, tenantId, fallback, ct).ConfigureAwait(false), min, max);
        }
        return new AutopilotLimits(v[0], v[1], v[2], v[3], TimeSpan.FromMinutes(v[4]), TimeSpan.FromSeconds(v[5]));
    }
}
