// AgentOs.Domain/Llm/LlmProviders.cs
// Provider names that callers outside the Llm module must recognise without referencing it.

using System;
using System.Collections.Generic;

namespace AgentOs.Domain.Llm;

/// <summary>Well-known <see cref="LlmResponse.Provider"/> values.</summary>
public static class LlmProviders
{
    /// <summary>Provider names an operator can pick in Settings (Anthropic is accepted as an alias of Claude).</summary>
    public static IReadOnlyList<string> Selectable { get; } = ["Claude", "Anthropic", "AzureOpenAI", "MAF", "RemoteAgent"];

    /// <summary>The keyless canned client (<c>OfflineLlmClient</c>). A response tagged with it never reached a model.</summary>
    public const string Offline = "Offline";

    /// <summary>True when <paramref name="provider"/> is the canned offline client — the answer is canned text,
    /// not model output, so it must never be presented (or counted) as a live run.</summary>
    public static bool IsOffline(string? provider) =>
        string.Equals(provider, Offline, StringComparison.OrdinalIgnoreCase);
}
