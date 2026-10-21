// AgentOs.Domain/Sessions/TaskSteps.cs
// The plain steps a coding task moves through, shown on the Board while the AI works (docs/plans/non-tech-ux.md #3).

using System;
using System.Collections.Generic;

namespace AgentOs.Domain.Sessions;

/// <summary>
/// Step keys as the paired runner reports them (it reads them off the CLI's event stream — see
/// <c>src/AgentOs.RemoteAgent/CliProgress.cs</c>, whose key list a test keeps identical) and their member-facing labels.
/// </summary>
public static class TaskSteps
{
    /// <summary>In the order a task normally passes through them.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("getting-code", "Getting the code"),
        ("reading", "Reading the code"),
        ("writing", "Writing the code"),
        ("building", "Checking it builds"),
        ("testing", "Running the tests"),
        ("saving", "Saving the change"),
        ("sending", "Sending the change for review"),
    ];

    /// <summary>The label for <paramref name="key"/>; null for a key this server doesn't know.</summary>
    public static string? LabelFor(string key)
    {
        foreach (var (k, label) in All)
        {
            if (string.Equals(k, key, StringComparison.Ordinal)) { return label; }
        }
        return null;
    }

    /// <summary>Position of the step whose label is <paramref name="label"/> (feed messages carry labels), or -1.</summary>
    public static int IndexOfLabel(string? label)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i].Label, label, StringComparison.Ordinal)) { return i; }
        }
        return -1;
    }
}

/// <summary>An <see cref="IProgress{T}"/> that reports inline, in order (System.Progress posts to a context and can
/// reorder steps).</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value) => report(value);
}
