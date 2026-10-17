// Token usage per token class — fresh input, prompt-cache reads, prompt-cache writes, output (+ reasoning) — so
// cost is priced per class instead of treating every prompt token as full-price input.
//
// Providers report usage differently; everything is normalised to the Microsoft.Extensions.AI convention
// (cache reads/writes ⊂ InputTokenCount, reasoning ⊂ OutputTokenCount):
//   • MEAI UsageDetails: CachedInputTokenCount / ReasoningTokenCount, already inside the input/output counts.
//   • Anthropic (raw MessageResponse.Usage): input_tokens EXCLUDES cache_read_input_tokens and
//     cache_creation_input_tokens — the three are added to get the real prompt size.
//   • AdditionalCounts: some adapters surface cache counts there (cache_read… / cache_creation…).
//
// Normalisation must happen PER TURN: in a tool-calling loop FunctionInvokingChatClient sums Usage across
// turns but keeps only the LAST turn's RawRepresentation. UsageNormalizingChatClient therefore sits beneath
// function invocation and normalises every turn, and MEAI's own summing (UsageDetails.Add, which also sums
// AdditionalCounts) carries the cache classes through the loop.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using Microsoft.Extensions.AI;

namespace AgentOs.Modules.Llm;

/// <summary>Normalises provider usage and reads it as <see cref="TokenUsage"/>.</summary>
public static class UsageReader
{
    /// <summary>AdditionalCounts key carrying prompt-cache WRITE tokens after normalisation (MEAI has no field).</summary>
    public const string CacheWriteKey = "agentos.cache_write_input_tokens";

    /// <summary>One turn's usage in the MEAI convention, folding in the provider's raw usage when richer.</summary>
    public static UsageDetails? Normalize(UsageDetails? usage, object? raw)
    {
        long input = usage?.InputTokenCount ?? 0;
        long output = usage?.OutputTokenCount ?? 0;
        long cacheRead = usage?.CachedInputTokenCount ?? 0;
        long cacheWrite = 0;
        long reasoning = usage?.ReasoningTokenCount ?? 0;

        if (usage?.AdditionalCounts is { } extra)
        {
            foreach (var (key, value) in extra)
            {
                if (key == CacheWriteKey || Has(key, "cache_creation") || Has(key, "CacheCreation") || Has(key, "cache_write"))
                {
                    cacheWrite = Math.Max(cacheWrite, value);
                }
                else if (cacheRead == 0 && (Has(key, "cache_read") || Has(key, "CacheRead")))
                {
                    cacheRead = value;
                }
            }
        }

        // Anthropic reports the cache classes beside input_tokens, not inside it.
        if (raw is Anthropic.SDK.Messaging.MessageResponse { Usage: { } au })
        {
            cacheRead = au.CacheReadInputTokens;
            cacheWrite = au.CacheCreationInputTokens;
            input = au.InputTokens + cacheRead + cacheWrite;
            output = au.OutputTokens;
        }

        if (usage is null && input == 0 && output == 0)
        {
            return null;
        }

        cacheRead = Math.Clamp(cacheRead, 0, input);
        cacheWrite = Math.Clamp(cacheWrite, 0, input - cacheRead);
        reasoning = Math.Clamp(reasoning, 0, output);

        var normalized = new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = input + output,
            CachedInputTokenCount = cacheRead,
            ReasoningTokenCount = reasoning,
        };
        if (cacheWrite > 0)
        {
            normalized.AdditionalCounts = new AdditionalPropertiesDictionary<long> { [CacheWriteKey] = cacheWrite };
        }
        return normalized;
    }

    /// <summary>Usage already in the MEAI convention (e.g. normalised per turn) as <see cref="TokenUsage"/>.</summary>
    public static TokenUsage Read(UsageDetails? usage)
    {
        if (usage is null)
        {
            return new TokenUsage(0, 0);
        }
        var write = usage.AdditionalCounts is { } extra && extra.TryGetValue(CacheWriteKey, out var w) ? w : 0;
        return new TokenUsage(
            Input: ToInt(usage.InputTokenCount ?? 0),
            Output: ToInt(usage.OutputTokenCount ?? 0),
            CacheRead: ToInt(usage.CachedInputTokenCount ?? 0),
            CacheWrite: ToInt(write),
            Reasoning: ToInt(usage.ReasoningTokenCount ?? 0));
    }

    /// <summary>A single-turn response's usage (normalises, then reads).</summary>
    public static TokenUsage Read(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Read(Normalize(response.Usage, response.RawRepresentation));
    }

    private static bool Has(string key, string part) => key.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static int ToInt(long v) => (int)Math.Clamp(v, 0, int.MaxValue);
}

/// <summary>Normalises each provider turn's usage (see <see cref="UsageReader"/>). Place it directly over the
/// provider client, beneath function invocation, so tool-calling loops keep per-turn cache detail.</summary>
public sealed class UsageNormalizingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        response.Usage = UsageReader.Normalize(response.Usage, response.RawRepresentation);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent uc)
                {
                    uc.Details = UsageReader.Normalize(uc.Details, update.RawRepresentation) ?? uc.Details;
                }
            }
            yield return update;
        }
    }
}
