// Compute cost by model + tokens. Built-in price table — list prices from the official Anthropic and OpenAI
// pricing pages (USD per 1M tokens, snapshot 2026-10-17); becomes UI-editable in Phase C (C.4) — this table
// stays the seeded default.

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Llm;

/// <summary>
/// Static cost calculator over a built-in price table.
/// <para>
/// <b>Lookup</b> is exact on the canonical model id via a <see cref="FrozenDictionary{TKey,TValue}"/> (O(1),
/// lock-free reads on every LLM call). Dated snapshots resolve to their base id —
/// <c>claude-sonnet-4-20250514</c>, <c>gpt-4o-mini-2024-07-18</c>, Vertex-style <c>claude-opus-4-5@20251101</c>
/// — unless the snapshot is itself priced (<c>gpt-4o-2024-05-13</c>); resolutions are memoised in a bounded
/// cache. There is deliberately <b>no</b> loose prefix match: <c>gpt-4.1</c> must never price
/// <c>gpt-4.1-mini</c> (5× too high), and a future <c>claude-opus-5-7</c> must not silently inherit
/// <c>claude-opus-5</c>'s rate.
/// </para>
/// <para>
/// <b>Long-context tiers:</b> some models bill a whole request at a higher rate once its prompt (uncached input
/// + cache reads + cache writes) exceeds a threshold — Claude Haiku 5.5 above 100k tokens, the GPT-5.4+ and
/// GPT-6 families above 272k.
/// </para>
/// <para>
/// <b>Unknown models fail closed:</b> they are priced at <see cref="UnpricedInputPerMillion"/> /
/// <see cref="UnpricedOutputPerMillion"/> (the top standard Claude tier), so an unpriced model can never slip
/// past the budget gate as $0. Callers log the estimate (see <see cref="IsKnown"/>) so the operator adds the
/// model or a pricing alias.
/// </para>
/// </summary>
public static class CostCalculator
{
    /// <summary>Fail-closed input rate for a model missing from the table (USD / 1M tokens).</summary>
    public const decimal UnpricedInputPerMillion = 15.00m;

    /// <summary>Fail-closed output rate for a model missing from the table (USD / 1M tokens).</summary>
    public const decimal UnpricedOutputPerMillion = 75.00m;

    private const int AnthropicLongPrompt = 100_000;
    private const int OpenAiLongPrompt = 272_000;

    // Anthropic: cache reads 0.1× input unless stated (0.025× Fable 5.1, 0.05× Opus 5.5 / Sonnet 5.5); 5-minute
    // cache writes 1.25× input. OpenAI: published cached-input rate; no cache-write surcharge (write = input).
    private static readonly ModelPricing[] Table =
    [
        // ── Anthropic — current ──
        A("claude-fable-5-1",  10.00m, 50.00m, cacheRead: 0.25m),
        A("claude-opus-5-5",    4.00m, 20.00m, cacheRead: 0.20m),
        A("claude-sonnet-5-5",  2.00m, 10.00m, cacheRead: 0.10m),
        A("claude-haiku-5-5",   0.10m,  0.50m) with
        {
            Long = new LongTier(AnthropicLongPrompt, 0.50m, 2.50m, CacheReadPerMillion: 0.05m, CacheWritePerMillion: 0.625m),
        },
        // ── Anthropic — legacy, still served ──
        A("claude-fable-5",    10.00m, 50.00m),
        A("claude-opus-5",      5.00m, 25.00m),
        A("claude-opus-4-8",    5.00m, 25.00m),
        A("claude-opus-4-7",    5.00m, 25.00m),
        A("claude-opus-4-6",    5.00m, 25.00m),
        A("claude-opus-4-5",    5.00m, 25.00m),
        A("claude-opus-4-1",   15.00m, 75.00m),
        A("claude-opus-4",     15.00m, 75.00m),
        A("claude-sonnet-5",    2.00m, 10.00m),
        A("claude-sonnet-4-6",  3.00m, 15.00m),
        A("claude-sonnet-4-5",  3.00m, 15.00m),
        A("claude-sonnet-4",    3.00m, 15.00m),
        A("claude-haiku-4-5",   1.00m,  5.00m),

        // ── OpenAI / Azure OpenAI ──
        O("gpt-6-astra",  10.00m, 1.00m,  50.00m, new LongTier(OpenAiLongPrompt, 20.00m, 75.00m, 2.00m, 20.00m)),
        O("gpt-6.1-sol",   2.00m, 0.10m,  10.00m, new LongTier(OpenAiLongPrompt,  4.00m, 15.00m, 0.20m,  4.00m)),
        O("gpt-6-sol",     2.00m, 0.20m,  10.00m, new LongTier(OpenAiLongPrompt,  4.00m, 15.00m, 0.40m,  4.00m)),
        O("gpt-6-luna",    0.10m, 0.01m,   0.50m, new LongTier(OpenAiLongPrompt,  0.20m,  0.75m, 0.02m,  0.20m)),
        O("gpt-5.6-sol",   4.00m, 0.40m,  20.00m, new LongTier(OpenAiLongPrompt,  8.00m, 30.00m, 0.80m,  8.00m)),
        O("gpt-5.6-terra", 2.00m, 0.20m,  12.00m, new LongTier(OpenAiLongPrompt,  4.00m, 18.00m, 0.40m,  4.00m)),
        O("gpt-5.6-luna",  0.20m, 0.02m,   1.20m, new LongTier(OpenAiLongPrompt,  0.40m,  1.80m, 0.04m,  0.40m)),
        O("gpt-5.5",       5.00m, 0.50m,  30.00m, new LongTier(OpenAiLongPrompt, 10.00m, 45.00m, 1.00m, 10.00m)),
        O("gpt-5.5-pro",  30.00m, 30.00m, 180.00m, new LongTier(OpenAiLongPrompt, 60.00m, 270.00m, 60.00m, 60.00m)),
        O("gpt-5.4",       2.50m, 0.25m,  15.00m, new LongTier(OpenAiLongPrompt,  5.00m, 22.50m, 0.50m,  5.00m)),
        O("gpt-5.4-mini",  0.75m, 0.075m,  4.50m),
        O("gpt-5.4-nano",  0.20m, 0.02m,   1.25m),
        O("gpt-5.4-pro",  30.00m, 30.00m, 180.00m, new LongTier(OpenAiLongPrompt, 60.00m, 270.00m, 60.00m, 60.00m)),
        O("gpt-5.2",       1.75m, 0.175m, 14.00m),
        O("gpt-5.2-pro",  21.00m, 21.00m, 168.00m),
        O("gpt-5.1",       1.25m, 0.125m, 10.00m),
        O("gpt-5",         1.25m, 0.125m, 10.00m),
        O("gpt-5-mini",    0.25m, 0.025m,  2.00m),
        O("gpt-5-nano",    0.05m, 0.005m,  0.40m),
        O("gpt-5-pro",    15.00m, 15.00m, 120.00m),
        O("gpt-4.1",       2.00m, 0.50m,   8.00m),
        O("gpt-4.1-mini",  0.40m, 0.10m,   1.60m),
        O("gpt-4.1-nano",  0.10m, 0.025m,  0.40m),
        O("gpt-4o",        2.50m, 1.25m,  10.00m),
        O("gpt-4o-2024-05-13", 5.00m, 5.00m, 15.00m),
        O("gpt-4o-mini",   0.15m, 0.075m,  0.60m),
        O("o1",           15.00m, 7.50m,  60.00m),
        O("o1-pro",      150.00m, 150.00m, 600.00m),
        O("o3-pro",       20.00m, 20.00m, 80.00m),
        O("o3",            2.00m, 0.50m,   8.00m),
        O("o4-mini",       1.10m, 0.275m,  4.40m),
        O("o3-mini",       1.10m, 0.55m,   4.40m),
    ];

    private static readonly FrozenDictionary<string, ModelPricing> ById =
        Table.ToFrozenDictionary(p => p.ModelId, StringComparer.OrdinalIgnoreCase);

    private static readonly ModelPricing Unpriced = A("unpriced", UnpricedInputPerMillion, UnpricedOutputPerMillion);

    // Memo of non-canonical ids (dated snapshots, deployment aliases) → resolved price or null. Bounded so a
    // stream of distinct junk ids can't grow it without limit; past the cap we just resolve without caching.
    private const int MemoCapacity = 1024;
    private static readonly ConcurrentDictionary<string, ModelPricing?> Memo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cost (USD) for one call. Unknown model → priced at the fail-closed rate.</summary>
    public static decimal Calculate(string model, int inputTokens, int outputTokens)
        => Calculate(model, inputTokens, outputTokens, cacheReadTokens: 0, cacheWriteTokens: 0);

    /// <summary>
    /// Cost (USD) for one call including prompt-cache traffic. <paramref name="inputTokens"/> is the uncached
    /// input only (Anthropic reports cache reads/writes separately from <c>input_tokens</c>). The prompt length
    /// that selects a long-context tier is <c>input + cacheRead + cacheWrite</c>.
    /// </summary>
    public static decimal Calculate(string model, int inputTokens, int outputTokens, int cacheReadTokens, int cacheWriteTokens)
    {
        if (string.IsNullOrWhiteSpace(model) || inputTokens < 0 || outputTokens < 0 || cacheReadTokens < 0 || cacheWriteTokens < 0)
        {
            return 0m;
        }

        var p = Resolve(model) ?? Unpriced;
        var promptTokens = (long)inputTokens + cacheReadTokens + cacheWriteTokens;
        var (inRate, outRate, readRate, writeRate) = p.Long is { } lt && promptTokens > lt.ThresholdTokens
            ? (lt.InputPerMillion, lt.OutputPerMillion, lt.CacheReadPerMillion, lt.CacheWritePerMillion)
            : (p.InputPerMillion, p.OutputPerMillion, p.CacheReadPerMillion, p.CacheWritePerMillion);

        var cost = (inputTokens * inRate
                  + outputTokens * outRate
                  + cacheReadTokens * readRate
                  + cacheWriteTokens * writeRate) / 1_000_000m;
        return Math.Round(cost, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>Cost (USD) for one call from normalised usage: fresh input, cache reads and cache writes are
    /// each priced at their own rate (see <see cref="UsageReader"/> for how usage is normalised).</summary>
    public static decimal Calculate(string model, TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return Calculate(model, usage.FreshInput, usage.Output, usage.CacheRead, usage.CacheWrite);
    }

    /// <summary>What the cache saved on this call: the same prompt billed entirely as fresh input, minus the
    /// actual cost. Positive when reads dominate; can be negative on a call that only writes the cache.</summary>
    public static decimal CacheSavings(string model, TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return Calculate(model, usage.Input, usage.Output) - Calculate(model, usage);
    }

    /// <summary>True if the model (or its dated snapshot) has a real price in the table. False means
    /// <see cref="Calculate(string,int,int)"/> used the fail-closed estimate.</summary>
    public static bool IsKnown(string model) => !string.IsNullOrWhiteSpace(model) && Resolve(model) is not null;

    /// <summary>Every priced model id, table order (current models first) — drives model pickers.</summary>
    public static IReadOnlyList<string> KnownModels { get; } = Array.ConvertAll(Table, p => p.ModelId);

    // Provider family → its priced models, built once (table order). Keys are the normalised family names.
    private static readonly FrozenDictionary<string, string[]> ByFamily = new Dictionary<string, string[]>
    {
        ["anthropic"] = Array.FindAll(Table, p => p.ModelId.StartsWith("claude-", StringComparison.Ordinal)).Select(p => p.ModelId).ToArray(),
        ["openai"] = Array.FindAll(Table, p => !p.ModelId.StartsWith("claude-", StringComparison.Ordinal)).Select(p => p.ModelId).ToArray(),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The model family a provider serves: <c>anthropic</c> (Claude/Anthropic) or <c>openai</c>
    /// (AzureOpenAI/MAF/OpenAI); null for providers that pick their own model (RemoteAgent, plugins).</summary>
    public static string? FamilyOf(string? provider) => provider?.Trim().ToUpperInvariant() switch
    {
        "CLAUDE" or "ANTHROPIC" => "anthropic",
        "AZUREOPENAI" or "AZURE" or "OPENAI" or "MAF" => "openai",
        _ => null,
    };

    /// <summary>Priced models for <paramref name="provider"/>'s family (O(1) lookup), or every model when the
    /// provider has no fixed family.</summary>
    public static IReadOnlyList<string> KnownModelsFor(string? provider) =>
        FamilyOf(provider) is { } family && ByFamily.TryGetValue(family, out var models) ? models : KnownModels;

    /// <summary>Base list price (USD per 1M input / output tokens) for a model, or null when unpriced.</summary>
    public static (decimal InputPerMillion, decimal OutputPerMillion)? ListPrice(string model) =>
        string.IsNullOrWhiteSpace(model) || Resolve(model) is not { } p ? null : (p.InputPerMillion, p.OutputPerMillion);

    /// <summary>The canonical table id a model (or dated snapshot) resolves to, or null when unpriced.
    /// Shared with <see cref="ModelCapabilities"/> so pricing and capability lookups never disagree.</summary>
    internal static string? CanonicalId(string model) =>
        string.IsNullOrWhiteSpace(model) ? null : Resolve(model)?.ModelId;

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

    // Anthropic entry: cache read defaults to 0.1× input, 5-minute cache write to 1.25× input.
    private static ModelPricing A(string id, decimal input, decimal output, decimal? cacheRead = null)
        => new(id, input, output, cacheRead ?? input * 0.1m, input * 1.25m);

    // OpenAI entry: explicit cached-input rate; cache writes bill at the plain input rate.
    private static ModelPricing O(string id, decimal input, decimal cachedInput, decimal output, LongTier? longTier = null)
        => new(id, input, output, cachedInput, input) { Long = longTier };

    private sealed record ModelPricing(
        string ModelId,
        decimal InputPerMillion,
        decimal OutputPerMillion,
        decimal CacheReadPerMillion,
        decimal CacheWritePerMillion)
    {
        /// <summary>Higher rates applied to the whole request once its prompt exceeds the threshold.</summary>
        public LongTier? Long { get; init; }
    }

    private sealed record LongTier(
        int ThresholdTokens,
        decimal InputPerMillion,
        decimal OutputPerMillion,
        decimal CacheReadPerMillion,
        decimal CacheWritePerMillion);
}
