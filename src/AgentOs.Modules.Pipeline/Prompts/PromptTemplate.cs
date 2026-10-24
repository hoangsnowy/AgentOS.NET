// Board agent prompts are per-run templates: the Reviewer's prompt names the repository and branch it checks out, the
// ticket Coding agent's names the branch it pushes. So a tenant can override them in Settings → Prompts like any other
// prompt, the template carries named placeholders ({owner}, {repo}, {branch}…) that are filled per run. One pass over
// the template: a value that itself contains "{branch}" (a repository name, a ticket title) is never expanded again,
// and an unknown {word} — or a JSON brace in the reply contract — is left exactly as written.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AgentOs.Modules.Pipeline.Prompts;

/// <summary>Fills a prompt template's named placeholders.</summary>
public static partial class PromptTemplate
{
    /// <summary><paramref name="template"/> with every known <c>{name}</c> replaced by its value (single pass).</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        return Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Placeholder();
}
