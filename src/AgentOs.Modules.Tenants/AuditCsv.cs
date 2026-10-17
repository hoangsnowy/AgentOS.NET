// CSV export of the audit trail (Settings → Audit log → Export). RFC 4180 quoting plus spreadsheet-formula
// neutralisation: a cell starting with = + - @ (or tab / CR) is prefixed with ' so Excel/Sheets never evaluate
// attacker-influenced text (a setting value, a username) as a formula (CSV injection, OWASP).

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgentOs.Modules.Tenants;

/// <summary>Renders <see cref="AuditEntry"/> rows as CSV.</summary>
public static class AuditCsv
{
    /// <summary>Header + one line per row, newest first as given.</summary>
    public static string ToCsv(IReadOnlyList<AuditEntry> rows)
    {
        System.ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder(64 + rows.Count * 96);
        sb.Append("timestamp_utc,tenant,user,action,detail\r\n");
        foreach (var r in rows)
        {
            sb.Append(r.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append(',');
            Cell(sb, r.TenantId).Append(',');
            Cell(sb, r.UserId).Append(',');
            Cell(sb, r.Action).Append(',');
            Cell(sb, r.Target).Append("\r\n");
        }
        return sb.ToString();
    }

    private static StringBuilder Cell(StringBuilder sb, string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            v = "'" + v;
        }
        if (v.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            return sb.Append(v);
        }
        return sb.Append('"').Append(v.Replace("\"", "\"\"", System.StringComparison.Ordinal)).Append('"');
    }
}
