// "Copy report" on System → About: a plain-text status report for a support ticket or a chat. Infrastructure
// facts (hosts, URLs, errors) are included only when the viewer may see them (platform admin).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgentOs.Web.Services.About;

/// <summary>Renders services as a copyable text report.</summary>
public static class AboutReport
{
    /// <summary>Plain-text report of <paramref name="services"/> checked at <paramref name="checkedAt"/>.</summary>
    public static string Text(IReadOnlyCollection<ServiceStatus> services, DateTimeOffset checkedAt, bool includeInfra)
    {
        ArgumentNullException.ThrowIfNull(services);
        var sb = new StringBuilder(1024);
        sb.Append(CultureInfo.InvariantCulture, $"AgentOS.NET status — {SystemSnapshot.Headline(services)}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Checked {checkedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC").AppendLine();
        foreach (var s in services)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"{s.Name} — {Label(s.Health)}");
            if (s.Version is { Length: > 0 } v) { sb.Append(CultureInfo.InvariantCulture, $" · {v}"); }
            if (s.LatencyMs is { } ms) { sb.Append(CultureInfo.InvariantCulture, $" · {Format.Latency(ms)}"); }
            sb.AppendLine();
            if (s.Summary is { Length: > 0 } summary) { sb.Append("  ").AppendLine(summary); }
            Facts(sb, s.Facts);
            if (includeInfra) { Facts(sb, s.Infra); }
        }
        return sb.ToString();
    }

    /// <summary>Human label for a health state.</summary>
    public static string Label(ServiceHealth health) => health switch
    {
        ServiceHealth.Healthy => "Healthy",
        ServiceHealth.Degraded => "Degraded",
        ServiceHealth.Down => "Down",
        _ => "Not in this run",
    };

    private static void Facts(StringBuilder sb, IReadOnlyList<ServiceFact> facts)
    {
        foreach (var f in facts)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {f.Label}: {f.Value}").AppendLine();
        }
    }
}
