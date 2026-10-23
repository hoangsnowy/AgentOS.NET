// The member's own AI plan (a Claude or Codex subscription on their paired computer) said "no more for now" — e.g.
// claude -p answering "You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)". That is not a failure of
// the work: retrying it, or sending it back to the Coding agent, only burns Autopilot's bounded tries while the plan
// is closed. So the RemoteAgent provider turns such a reply into a typed signal — a PlanLimit (with the reset time
// when the CLI says it), thrown as PlanLimitException and recorded per member in IPlanLimits — and Autopilot waits
// it out (ProjectOrchestrator) instead of spending retries. Detection is pure and tested against real-shaped CLI
// lines; it only looks at the tail of a reply, and only at a short one when the CLI exited cleanly, so code or an
// answer that merely mentions a "usage limit" is never mistaken for one.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using AgentOs.Domain.Llm;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>A Claude / Codex plan limit the member's computer reported.</summary>
/// <param name="Plan">The plan, in plain words: <c>Claude</c> or <c>Codex</c>.</param>
/// <param name="Message">The CLI's own words.</param>
/// <param name="ResetsAt">When the plan takes work again; null when the CLI didn't say (or said it in a way that
/// can't be placed in time). Carries the offset of the zone the CLI named, when it named one.</param>
/// <param name="InNamedZone">True when <paramref name="ResetsAt"/> is a wall-clock time in the zone the CLI named
/// (shown as is, "23:50"); false when it was computed (a duration, a timestamp) and is shown in UTC.</param>
public sealed record PlanLimit(string Plan, string Message, DateTimeOffset? ResetsAt, bool InNamedZone = false)
{
    /// <summary>The reset time as the member reads it ("23:50", or "16:50 UTC"); null when unknown.</summary>
    public string? ResetText => ResetsAt is not { } at
        ? null
        : InNamedZone
            ? at.ToString("HH:mm", CultureInfo.InvariantCulture)
            : at.ToUniversalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>Autopilot's wait for a plan limit: the limit, and when work may start again.</summary>
public sealed record PlanLimitHold(PlanLimit Limit, DateTimeOffset UntilUtc)
{
    /// <summary>When Autopilot carries on, as the member reads it: the plan's reset time when known, else the time
    /// Autopilot checks again.</summary>
    public string WaitsUntilText => Limit.ResetText ?? UntilUtc.ToUniversalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>The one plain line the Autopilot activity and the Board show.</summary>
    public string Sentence => Limit.ResetText is not null
        ? $"Your computer's {Limit.Plan} plan hit its limit — Autopilot waits until {WaitsUntilText}, nothing is lost."
        : $"Your computer's {Limit.Plan} plan hit its limit — Autopilot checks again at {WaitsUntilText}, nothing is lost.";
}

/// <summary>Reads a plan limit out of what a CLI answered.</summary>
public static partial class PlanLimitDetector
{
    /// <summary>Only the end of a reply is read: a CLI prints the limit as its last words.</summary>
    internal const int TailLength = 600;

    /// <summary>A clean (exit 0) reply longer than this is real work, never a limit notice.</summary>
    internal const int MaxCleanReplyLength = 600;

    /// <summary>The longest wait believed: a weekly limit, with a day to spare.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromDays(8);

    [GeneratedRegex(@"\b(?:hit|reached)\s+your\s+(?:(?:current|session|usage|weekly|daily|monthly|5-hour|five-hour|opus|sonnet|plan|subscription)\s+)*limit\b"
        + @"|\b(?:usage|session|weekly|daily|monthly|5-hour|opus|sonnet)\s+limit\s+reached\b|\busage\s+limit\s+exceeded\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HitPattern();

    // "Claude AI usage limit reached|1761234567" — older Claude Code prints the reset as a Unix timestamp.
    [GeneratedRegex(@"limit\s+reached\|(?<epoch>\d{10})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpochPattern();

    // "resets 11:50pm (Asia/Ho_Chi_Minh)", "resets 7pm", "resets Oct 27, 5pm (Europe/Berlin)", "try again at 3:04 PM".
    [GeneratedRegex(@"\b(?:resets?|try\s+again\s+at|available\s+again\s+at)\s+(?:at\s+)?(?:on\s+)?"
        + @"(?:(?<mon>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?:at\s+)?)?"
        + @"(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>[ap]\.?\s?m\b\.?)?(?:\s*\((?<tz>[^)\s]+)\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClockPattern();

    // "try again in 2 hours 5 minutes", "resets in 3h 12m", "try again in 4 days".
    [GeneratedRegex(@"\b(?:try\s+again|resets?|available\s+again)\s+in\s+(?<dur>(?:\d+\s*(?:days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b[\s,]*(?:and\s+)?)+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"(?<n>\d+)\s*(?<u>days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPart();

    /// <summary>True when <paramref name="text"/> (a stored task error, a CLI reply) is a plan-limit notice.</summary>
    public static bool IsPlanLimit(string? text) => !string.IsNullOrWhiteSpace(text) && HitPattern().IsMatch(TailOf(text));

    /// <summary>The plan limit a runner's reply reports, or null. A clean reply counts only when it is short — a real
    /// answer that mentions a limit is work, not a notice.</summary>
    /// <param name="ok">The CLI exited cleanly.</param>
    /// <param name="content">Its answer.</param>
    /// <param name="error">Its error text.</param>
    /// <param name="cli">The CLI the run used ("claude", "codex"; null = the runner's default).</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="zones">Resolves the zone a CLI names (null = the system's IANA zones).</param>
    public static PlanLimit? FromReply(bool ok, string? content, string? error, string? cli, DateTimeOffset nowUtc,
        Func<string, TimeZoneInfo?>? zones = null)
    {
        if (ok)
        {
            return content is { Length: > 0 and <= MaxCleanReplyLength } ? Detect(content, cli, nowUtc, zones) : null;
        }
        return Detect(error, cli, nowUtc, zones);
    }

    /// <summary>The plan limit <paramref name="text"/> reports, or null.</summary>
    public static PlanLimit? Detect(string? text, string? cli, DateTimeOffset nowUtc, Func<string, TimeZoneInfo?>? zones = null)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var tail = TailOf(text);
        var hit = HitPattern().Match(tail);
        if (!hit.Success) { return null; }
        var plan = PlanOf(cli, tail);
        var (resetsAt, named) = ResetOf(tail[hit.Index..], nowUtc, zones ?? SystemZone);
        if (resetsAt is { } r && (r <= nowUtc || r - nowUtc > MaxWait)) { resetsAt = null; named = false; }
        // The CLI's own line (an "exit 1:" prefix and earlier output stay out of it).
        var start = hit.Index == 0 ? 0 : tail.LastIndexOf('\n', hit.Index - 1) + 1;
        var end = tail.IndexOf('\n', hit.Index);
        var message = tail[start..(end < 0 ? tail.Length : end)].Trim();
        if (message.StartsWith("exit ", StringComparison.Ordinal) && message.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0 and < 12)
        {
            message = message[(colon + 2)..].Trim();
        }
        return new PlanLimit(plan, message, resetsAt, named);
    }

    private static string TailOf(string text) => text.Length <= TailLength ? text : text[^TailLength..];

    private static string PlanOf(string? cli, string text) =>
        cli?.Contains("codex", StringComparison.OrdinalIgnoreCase) == true
        || text.Contains("codex", StringComparison.OrdinalIgnoreCase) || text.Contains("chatgpt", StringComparison.OrdinalIgnoreCase)
            ? "Codex"
            : "Claude";

    private static (DateTimeOffset? At, bool Named) ResetOf(string text, DateTimeOffset nowUtc, Func<string, TimeZoneInfo?> zones)
    {
        if (EpochPattern().Match(text) is { Success: true } e
            && long.TryParse(e.Groups["epoch"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return (DateTimeOffset.FromUnixTimeSeconds(seconds), false);
        }
        if (DurationPattern().Match(text) is { Success: true } d)
        {
            var wait = TimeSpan.Zero;
            foreach (Match part in DurationPart().Matches(d.Groups["dur"].Value))
            {
                var n = int.Parse(part.Groups["n"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
                var unit = part.Groups["u"].Value.ToUpperInvariant();
                wait += unit[0] switch
                {
                    'D' => TimeSpan.FromDays(n),
                    'H' => TimeSpan.FromHours(n),
                    'S' => TimeSpan.FromSeconds(n),
                    _ => TimeSpan.FromMinutes(n),
                };
            }
            return wait > TimeSpan.Zero ? (nowUtc + wait, false) : (null, false);
        }
        if (ClockPattern().Match(text) is not { Success: true } c) { return (null, false); }
        var hour = int.Parse(c.Groups["h"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        var minute = c.Groups["m"].Success ? int.Parse(c.Groups["m"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        if (c.Groups["ap"].Success)
        {
            if (hour is < 1 or > 12) { return (null, false); }
            var pm = c.Groups["ap"].Value.StartsWith('p') || c.Groups["ap"].Value.StartsWith('P');
            hour = hour % 12 + (pm ? 12 : 0);
        }
        if (hour > 23 || minute > 59) { return (null, false); }
        // A wall-clock time is placed only in a zone the CLI named: the server doesn't know the computer's zone.
        var zone = c.Groups["tz"].Success ? zones(c.Groups["tz"].Value) : null;
        if (zone is null) { return (null, false); }
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var date = local.Date;
        var explicitDate = false;
        if (c.Groups["mon"].Success && c.Groups["day"].Success)
        {
            var month = Array.IndexOf(Months, c.Groups["mon"].Value[..3].ToUpperInvariant()) + 1;
            var day = int.Parse(c.Groups["day"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
            if (month < 1 || day < 1 || day > DateTime.DaysInMonth(date.Year, month)) { return (null, false); }
            date = new DateTime(date.Year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
            explicitDate = true;
        }
        var wall = DateTime.SpecifyKind(date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        // "resets 11:50pm" said at 23:55 means tomorrow; a dated reset already past means next year.
        if (wall <= local.DateTime) { wall = explicitDate ? wall.AddYears(1) : wall.AddDays(1); }
        return (new DateTimeOffset(wall, zone.GetUtcOffset(wall)), true);
    }

    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static TimeZoneInfo? SystemZone(string id)
    {
        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase) || string.Equals(id, "GMT", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) && TryFind(windowsId, out var zone) ? zone : null;
        }
    }

    private static bool TryFind(string id, out TimeZoneInfo? zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = null;
            return false;
        }
    }
}

/// <summary>A runner's reply was a plan-limit notice: the work didn't happen, and nothing about it failed.</summary>
public sealed class PlanLimitException : LlmException
{
    /// <summary>The limit.</summary>
    public PlanLimit? Limit { get; }

    public PlanLimitException() { }

    public PlanLimitException(string message) : base(message) { }

    public PlanLimitException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>The limit <paramref name="limit"/>, reported by <paramref name="provider"/>.</summary>
    public PlanLimitException(PlanLimit limit, string provider) : base((limit ?? throw new ArgumentNullException(nameof(limit))).Message, provider)
    {
        Limit = limit;
    }
}

/// <summary>Plan limits per member (their computer's plan), for Autopilot to wait out and the Board to show.</summary>
public interface IPlanLimits
{
    /// <summary>Record that <paramref name="memberUserId"/>'s plan hit <paramref name="limit"/>.</summary>
    void Report(string tenantId, string memberUserId, PlanLimit limit);

    /// <summary>The member's active wait, or null when their plan is open.</summary>
    PlanLimitHold? Current(string tenantId, string memberUserId);
}

/// <summary>In-memory <see cref="IPlanLimits"/>. Singleton; thread-safe. A restart forgets a wait — the next run hits
/// the limit again (a cheap, immediate answer) and the wait is back.</summary>
public sealed class PlanLimitTracker(TimeProvider time) : IPlanLimits
{
    /// <summary>Work starts this long after the reset, so the first call isn't refused at the stroke of it.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    /// <summary>The wait when the CLI didn't say when the plan resets: check again after this long.</summary>
    public static readonly TimeSpan UnknownResetWait = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<(string Tenant, string Member), PlanLimitHold> _holds = new();

    /// <inheritdoc />
    public void Report(string tenantId, string memberUserId, PlanLimit limit)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(limit);
        var until = (limit.ResetsAt ?? time.GetUtcNow() + UnknownResetWait).ToUniversalTime() + Grace;
        _holds[(tenantId, memberUserId ?? string.Empty)] = new PlanLimitHold(limit, until);
    }

    /// <inheritdoc />
    public PlanLimitHold? Current(string tenantId, string memberUserId)
    {
        var key = (tenantId ?? string.Empty, memberUserId ?? string.Empty);
        if (!_holds.TryGetValue(key, out var hold)) { return null; }
        if (hold.UntilUtc > time.GetUtcNow()) { return hold; }
        // Only this hold: a newer report that raced in stays.
        _holds.TryRemove(new System.Collections.Generic.KeyValuePair<(string, string), PlanLimitHold>(key, hold));
        return null;
    }
}
