// Compute cost by model + tokens. Built-in price table (list prices, USD per 1M tokens, Oct 2026 snapshot);
// becomes UI-editable in Phase C (C.4) — this table stays the seeded default.

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Linq;

namespace AgentOs.Modules.Llm;

/// <summary>
/// Static cost calculator over a built-in price table.
/// <para>
/// <b>Lookup</b> is exact on the canonical model id via a <see cref="FrozenDictionary{TKey,TValue}"/> (O(1),
/// lock-free reads on every LLM call). Dated snapshots resolve to their base id —
/// <c>claude-sonnet-4-20250514</c>, <c>gpt-4o-mini-2024-07-18</c>, Vertex-style <c>claude-opus-4-5@20251101</c>
/// — and resolutions are memoised in a bounded cache. There is deliberately <b>no</b> loose prefix match:
/// <c>gpt-4.1</c> must never price <c>gpt-4.1-mini</c> (5× too high), and a future <c>claude-opus-5-7</c> must
/// not silently inherit <c>claude-opus-5</c>'s rate.
/// </para>
/// <para>
/// <b>Unknown models fail closed:</b> they are priced at the most expensive rate in the table (see
/// <see cref="IsKnown"/>), so an unpriced model can never slip past the budget gate as $0. Callers log the
/// estimate so the operator adds the model (or a pricing alias) to the table.
/// </para>
/// </summary>
public static class CostCalculator
{
    // Anthropic: cache reads ≈ 0.1× input (0.025× on Fable 5.1, 0.05× on Opus 5.5); 5-minute cache writes 1.25×.
    // OpenAI / Azure OpenAI: cached input at the published cached rate; no cache-write surcharge.
    private static readonly ModelPricing[] Table =
    [
        new("claude-fable-5-1",  10.00m, 50.00m, CacheReadPerMillion: 0.25m),
        new("claude-fable-5",    10.00m, 50.00m),
        new("claude-opus-5-5",    4.00m, 20.00m, CacheReadPerMillion: 0.20m),
        new("claude-opus-5",      5.00m, 25.00m),
        new("claude-opus-4-8",    5.00m, 25.00m),
        new("claude-opus-4-7",    5.00m, 25.00m),
        new("claude-opus-4-6",    5.00m, 25.00m),
        new("claude-opus-4-5",    5.00m, 25.00m),
        new("claude-opus-4-1",   15.00m, 75.00m),
        new("claude-opus-4",     15.00m, 75.00m),
        new("claude-sonnet-5",    2.00m, 10.00m),
        new("claude-sonnet-4-6",  3.00m, 15.00m),
        new("claude-sonnet-4-5",  3.00m, 15.00m),
        new("claude-sonnet-4",    3.00m, 15.00m),
        new("claude-haiku-4-5",   1.00m,  5.00m),
        new("gpt-4.1",            2.00m,  8.00m, CacheReadPerMillion: 0.50m,  CacheWritePerMillion: 2.00m),
        new("gpt-4.1-mini",       0.40m,  1.60m, CacheReadPerMillion: 0.10m,  CacheWritePerMillion: 0.40m),
        new("gpt-4.1-nano",       0.10m,  0.40m, CacheReadPerMillion: 0.025m, CacheWritePerMillion: 0.10m),
        new("gpt-4o",             2.50m, 10.00m, CacheReadPerMillion: 1.25m,  CacheWritePerMillion: 2.50m),
        new("gpt-4o-mini",        0.15m,  0.60m, CacheReadPerMillion: 0.075m, CacheWritePerMillion: 0.15m),
    ];

    private static readonly FrozenDictionary<string, ModelPricing> ById =
        Table.ToFrozenDictionary(p => p.ModelId, StringComparer.OrdinalIgnoreCase);

    /// <summary>The fail-closed rate for an unpriced model: the most expensive input and output rates in the table.</summary>
    private static readonly ModelPricing Ceiling = new(
        "unpriced",
        Table.Max(p => p.InputPerMillion),
        Table.Max(p => p.OutputPerMillion),
        Table.Max(p => p.CacheReadRate),
        Table.Max(p => p.CacheWriteRate));

    // Memo of non-canonical ids (dated snapshots, deployment aliases) → resolved price or null. Bounded so a
    // stream of distinct junk ids can't grow it without limit; past the cap we just resolve without caching.
    private const int MemoCapacity = 1024;
    private static readonly ConcurrentDictionary<string, ModelPricing?> Memo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cost (USD) for one call. Unknown model → priced at the table's ceiling rate (fail-closed).</summary>
    public static decimal Calculate(string model, int inputTokens, int outputTokens)
        => Calculate(model, inputTokens, outputTokens, cacheReadTokens: 0, cacheWriteTokens: 0);

    /// <summary>
    /// Cost (USD) for one call including prompt-cache traffic. <paramref name="inputTokens"/> is the uncached
    /// input only (Anthropic reports cache reads/writes separately from <c>input_tokens</c>).
    /// </summary>
    public static decimal Calculate(string model, int inputTokens, int outputTokens, int cacheReadTokens, int cacheWriteTokens)
    {
        if (string.IsNullOrWhiteSpace(model) || inputTokens < 0 || outputTokens < 0 || cacheReadTokens < 0 || cacheWriteTokens < 0)
        {
            return 0m;
        }

        var p = Resolve(model) ?? Ceiling;
        var cost = (inputTokens * p.InputPerMillion
                  + outputTokens * p.OutputPerMillion
                  + cacheReadTokens * p.CacheReadRate
                  + cacheWriteTokens * p.CacheWriteRate) / 1_000_000m;
        return Math.Round(cost, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>True if the model (or its dated snapshot) has a real price in the table. False means
    /// <see cref="Calculate(string,int,int)"/> used the fail-closed ceiling estimate.</summary>
    public static bool IsKnown(string model) => !string.IsNullOrWhiteSpace(model) && Resolve(model) is not null;

    private static ModelPricing? Resolve(string model)
    {
        var id = model.Trim();
        if (ById.TryGetValue(id, out var exact))
        {
            return exact;
        }
        if (Memo.TryGetValue(id, out var memo))
        {
            return memo;
        }

        var resolved = StripSnapshot(id) is { } baseId && ById.TryGetValue(baseId, out var p) ? p : null;
        if (Memo.Count < MemoCapacity)
        {
            Memo.TryAdd(id, resolved);
        }
        return resolved;
    }

    // Returns the base id of a dated snapshot, or null when the id carries no recognised snapshot suffix.
    // Accepts "<id>@<anything>" (Vertex), "<id>-YYYYMMDD" (Anthropic) and "<id>-YYYY-MM-DD" (OpenAI).
    internal static string? StripSnapshot(string id)
    {
        var at = id.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            return id[..at];
        }

        var span = id.AsSpan();
        if (span.Length > 9 && span[^9] == '-' && IsDigits(span[^8..]))
        {
            return id[..^9];
        }

        if (span.Length > 11 && span[^11] == '-' && IsDigits(span[^10..^6]) && span[^6] == '-'
            && IsDigits(span[^5..^3]) && span[^3] == '-' && IsDigits(span[^2..]))
        {
            return id[..^11];
        }

        return null;
    }

    private static bool IsDigits(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return s.Length > 0;
    }

    private sealed record ModelPricing(
        string ModelId,
        decimal InputPerMillion,
        decimal OutputPerMillion,
        decimal? CacheReadPerMillion = null,
        decimal? CacheWritePerMillion = null)
    {
        // Defaults when the table doesn't state a cache rate: Anthropic-style 0.1× read, 1.25× 5-minute write.
        public decimal CacheReadRate => CacheReadPerMillion ?? InputPerMillion * 0.1m;
        public decimal CacheWriteRate => CacheWritePerMillion ?? InputPerMillion * 1.25m;
    }
}
