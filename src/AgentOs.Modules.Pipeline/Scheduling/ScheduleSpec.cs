// Scheduled runs (competitor parity, docs/plans/competitive-parity.md §3 item 3): WHEN a saved story runs. Either
// once at an instant, or on a cron expression (5-field, UTC) — the Home presets ("every day at 09:00", "weekdays",
// "every Monday") are cron underneath. Cronos (0.13.0) parses and steps the expression. A schedule may not fire
// more often than every 15 minutes: every run spends real tokens, and a typo like "* * * * *" must not become 1 440
// pipeline runs a day. Times are UTC and labelled as such (the server has no reliable viewer time zone, and the
// Web runs with InvariantGlobalization, so IANA zone lookup isn't portable).

using System.Globalization;
using Cronos;

namespace AgentOs.Modules.Pipeline.Scheduling;

/// <summary>How a schedule repeats.</summary>
public enum ScheduleKind
{
    /// <summary>Exactly once, at <see cref="ScheduleSpec.RunAtUtc"/>.</summary>
    Once,

    /// <summary>On a 5-field cron expression, UTC.</summary>
    Cron,
}

/// <summary>When a scheduled run fires.</summary>
/// <param name="Kind">Once or cron.</param>
/// <param name="Cron">The cron expression (Kind = Cron).</param>
/// <param name="RunAtUtc">The instant (Kind = Once).</param>
public sealed record ScheduleSpec(ScheduleKind Kind, string? Cron, DateTimeOffset? RunAtUtc)
{
    /// <summary>The shortest allowed gap between two runs of one schedule.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);

    /// <summary>A one-off run.</summary>
    public static ScheduleSpec At(DateTimeOffset instant) => new(ScheduleKind.Once, null, instant.ToUniversalTime());

    /// <summary>A cron schedule.</summary>
    public static ScheduleSpec Every(string cron) => new(ScheduleKind.Cron, cron?.Trim(), null);

    /// <summary>Every day at <paramref name="time"/> UTC.</summary>
    public static ScheduleSpec Daily(TimeOnly time) => Every(FormattableString.Invariant($"{time.Minute} {time.Hour} * * *"));

    /// <summary>Monday–Friday at <paramref name="time"/> UTC.</summary>
    public static ScheduleSpec Weekdays(TimeOnly time) => Every(FormattableString.Invariant($"{time.Minute} {time.Hour} * * 1-5"));

    /// <summary>Every <paramref name="day"/> at <paramref name="time"/> UTC.</summary>
    public static ScheduleSpec Weekly(DayOfWeek day, TimeOnly time) => Every(FormattableString.Invariant($"{time.Minute} {time.Hour} * * {(int)day}"));

    /// <summary>Null when valid at <paramref name="now"/>; otherwise a user-facing reason.</summary>
    public string? Validate(DateTimeOffset now)
    {
        if (Kind == ScheduleKind.Once)
        {
            return RunAtUtc is null ? "Pick a date and time."
                : RunAtUtc.Value <= now ? "Pick a time in the future."
                : null;
        }
        if (string.IsNullOrWhiteSpace(Cron)) { return "Enter a cron expression."; }
        CronExpression expression;
        try { expression = CronExpression.Parse(Cron); }
        catch (CronFormatException ex) { return $"Not a valid cron expression: {ex.Message}"; }

        var first = expression.GetNextOccurrence(now.UtcDateTime);
        if (first is null) { return "This cron expression never fires."; }
        var second = expression.GetNextOccurrence(first.Value);
        // Check a day of gaps: a schedule that is fine at noon but fires every minute at night still fails.
        for (var i = 0; second is not null && i < 96; i++)
        {
            if (second.Value - first.Value < MinimumInterval)
            {
                return $"Runs at most every {MinimumInterval.TotalMinutes:0} minutes — each run spends real tokens.";
            }
            first = second;
            second = expression.GetNextOccurrence(second.Value);
        }
        return null;
    }

    /// <summary>The next fire time strictly after <paramref name="after"/>, or null when there is none.</summary>
    public DateTimeOffset? NextAfter(DateTimeOffset after)
    {
        if (Kind == ScheduleKind.Once) { return RunAtUtc is { } at && at > after ? at : null; }
        if (string.IsNullOrWhiteSpace(Cron)) { return null; }
        try
        {
            var next = CronExpression.Parse(Cron).GetNextOccurrence(after.UtcDateTime);
            return next is null ? null : new DateTimeOffset(next.Value, TimeSpan.Zero);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    /// <summary>A short human label: "Once · 2026-10-20 09:00 UTC", "Every day at 09:00 UTC", "Cron 0 */6 * * *".</summary>
    public string Describe()
    {
        if (Kind == ScheduleKind.Once)
        {
            return RunAtUtc is { } at ? $"Once · {at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC" : "Once";
        }
        var parts = (Cron ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 5 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            && parts[2] == "*" && parts[3] == "*" && m is >= 0 and < 60 && h is >= 0 and < 24)
        {
            var at = FormattableString.Invariant($"{h:00}:{m:00} UTC");
            if (parts[4] == "*") { return $"Every day at {at}"; }
            if (parts[4] == "1-5") { return $"Weekdays at {at}"; }
            if (int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 6)
            {
                return $"Every {(DayOfWeek)d} at {at}";
            }
        }
        return $"Cron {Cron} (UTC)";
    }
}
