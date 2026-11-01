// Argument parsing for the compare mode (Program.cs). Pure BCL so AgentOs.Tests compiles this file directly.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AgentOs.Benchmarks.Comparison;

/// <summary>Validation helpers for <c>compare</c>'s command line.</summary>
internal static class ComparerArguments
{
    /// <summary>Options that take a value (the next argument is never positional).</summary>
    public static readonly IReadOnlySet<string> ValueOptions =
        new HashSet<string>(["--threshold", "--noise-floor-ns", "--alloc-floor-bytes", "--timing", "--summary"], StringComparer.Ordinal);

    /// <summary>The non-option arguments, skipping each value-taking option's value.</summary>
    public static string[] Positional(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !ValueOptions.Contains(args[i - 1]))).ToArray();
    }

    /// <summary>Parse <c>--threshold</c>: a fraction in (0, 1] (<c>0.20</c>) or a percentage in (0, 100] with a
    /// <c>%</c> suffix (<c>20%</c>). A bare number above 1 is rejected — <c>20</c> would otherwise mean 2000 %.
    /// Null (the option is absent) yields the default 0.20.</summary>
    public static bool TryParseThreshold(string? text, out double fraction, out string? error)
    {
        fraction = 0.20;
        error = null;
        if (text is null)
        {
            return true;
        }
        var trimmed = text.Trim();
        var percent = trimmed.EndsWith('%');
        var number = percent ? trimmed[..^1].TrimEnd() : trimmed;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            error = $"--threshold must be a fraction (0.20) or a percentage (20%), got '{text}'.";
            return false;
        }
        var candidate = percent ? value / 100 : value;
        if (candidate is <= 0 or > 1)
        {
            error = percent
                ? $"--threshold percentage must be in (0%, 100%], got '{text}'."
                : $"--threshold fraction must be in (0, 1] (use '{value.ToString(CultureInfo.InvariantCulture)}%' for a percentage), got '{text}'.";
            return false;
        }
        fraction = candidate;
        return true;
    }

    /// <summary>Escape a GitHub Actions workflow-command message (<c>%</c>, CR, LF).</summary>
    public static string EscapeAnnotation(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var sb = new StringBuilder(message.Length);
        foreach (var c in message)
        {
            _ = c switch
            {
                '%' => sb.Append("%25"),
                '\r' => sb.Append("%0D"),
                '\n' => sb.Append("%0A"),
                _ => sb.Append(c),
            };
        }
        return sb.ToString();
    }
}
