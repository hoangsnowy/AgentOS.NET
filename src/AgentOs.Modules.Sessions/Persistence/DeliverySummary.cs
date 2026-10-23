// Delivery quality of a tenant's AI tasks — the numbers that say whether the agents (and the lessons they learn) get
// better: how many tasks merged, how many on the first try (no change ever sent back), how many fix rounds a task
// needs, and how long a ticket takes from task start to merge.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentOs.Modules.Sessions.Persistence;

/// <summary>Delivery of merged tasks in a period, plus the tasks stuck right now.</summary>
/// <param name="Merged">Tasks whose changes all merged.</param>
/// <param name="FirstTry">Of those, merged without a single change sent back.</param>
/// <param name="AverageFixRounds">Mean fix rounds per merged task.</param>
/// <param name="MedianHoursToMerge">Median hours from task start to merge.</param>
/// <param name="Stuck">Tasks that couldn't finish or wait for a person, now.</param>
public sealed record DeliverySummary(int Merged, int FirstTry, double AverageFixRounds, double MedianHoursToMerge, int Stuck)
{
    /// <summary>No data.</summary>
    public static DeliverySummary Empty { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Share of merged tasks that passed on the first try (0–1); 0 when nothing merged.</summary>
    public double FirstTryRate => Merged == 0 ? 0 : (double)FirstTry / Merged;

    /// <summary>Summarise merged tasks given as (fix rounds, hours to merge).</summary>
    public static DeliverySummary From(IReadOnlyList<(int FixRounds, double HoursToMerge)> merged, int stuck)
    {
        ArgumentNullException.ThrowIfNull(merged);
        if (merged.Count == 0) { return Empty with { Stuck = stuck }; }
        var hours = merged.Select(m => Math.Max(0, m.HoursToMerge)).Order().ToList();
        var median = hours.Count % 2 == 1 ? hours[hours.Count / 2] : (hours[hours.Count / 2 - 1] + hours[hours.Count / 2]) / 2;
        return new DeliverySummary(merged.Count, merged.Count(m => m.FixRounds == 0), merged.Average(m => m.FixRounds), median, stuck);
    }
}
