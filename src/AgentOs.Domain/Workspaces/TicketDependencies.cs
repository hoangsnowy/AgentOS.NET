// AgentOs.Domain/Workspaces/TicketDependencies.cs
// Which tickets must be done before this one — read from the ticket body, written there by "Start from idea".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentOs.Domain.Workspaces;

/// <summary>
/// "Depends on #8, #11" / "Blocked by #3" lines in a ticket body. The board runs a ticket only when every ticket it
/// depends on is closed (its pull request merged with <c>Closes #N</c>), so a module is built before its consumers
/// instead of racing them. Plain text on purpose: a person can read and edit it on GitHub.
/// </summary>
public static partial class TicketDependencies
{
    /// <summary>The line "Start from idea" appends to a ticket body.</summary>
    public static string Line(IEnumerable<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        var list = numbers.Distinct().Order().Select(n => "#" + n.ToString(CultureInfo.InvariantCulture)).ToList();
        return list.Count == 0 ? string.Empty : "Depends on " + string.Join(", ", list);
    }

    /// <summary>Issue numbers named on "Depends on" / "Blocked by" / "Requires" lines (any case, optional colon or bold).</summary>
    public static IReadOnlyList<int> Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) { return []; }
        var result = new SortedSet<int>();
        foreach (Match line in DependencyLine().Matches(body))
        {
            foreach (Match number in IssueRef().Matches(line.Groups["refs"].Value))
            {
                if (int.TryParse(number.Groups["n"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                {
                    result.Add(n);
                }
            }
        }
        return [.. result];
    }

    /// <summary>
    /// Orders draft indices so every ticket comes after the ones it depends on (Kahn's algorithm, stable by index).
    /// Out-of-range, self and cyclic edges are dropped — a bad plan must never block ticket creation.
    /// </summary>
    public static IReadOnlyList<int> CreationOrder(int count, Func<int, IReadOnlyList<int>?> dependsOn)
    {
        ArgumentNullException.ThrowIfNull(dependsOn);
        var edges = new List<int>[count];
        var indegree = new int[count];
        for (var i = 0; i < count; i++) { edges[i] = []; }
        for (var i = 0; i < count; i++)
        {
            foreach (var d in (dependsOn(i) ?? []).Distinct())
            {
                if (d < 0 || d >= count || d == i) { continue; }
                edges[d].Add(i);
                indegree[i]++;
            }
        }
        var ready = new PriorityQueue<int, int>();
        for (var i = 0; i < count; i++) { if (indegree[i] == 0) { ready.Enqueue(i, i); } }
        var order = new List<int>(count);
        var placed = new bool[count];
        while (ready.TryDequeue(out var i, out _))
        {
            order.Add(i);
            placed[i] = true;
            foreach (var j in edges[i])
            {
                if (--indegree[j] == 0) { ready.Enqueue(j, j); }
            }
        }
        // Whatever is left sits on a cycle: append in index order (their cyclic edges are then ignored).
        for (var i = 0; i < count; i++) { if (!placed[i]) { order.Add(i); } }
        return order;
    }

    [GeneratedRegex(@"^[ \t>*_-]*(?:\*\*)?(?:depends on|blocked by|requires)(?:\*\*)?\s*:?\s*(?<refs>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DependencyLine();

    [GeneratedRegex(@"(?<![\w/])#(?<n>\d{1,7})\b", RegexOptions.CultureInvariant)]
    private static partial Regex IssueRef();
}
