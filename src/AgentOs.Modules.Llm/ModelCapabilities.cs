// Request-shape capabilities per model. Newer reasoning models reject sampling parameters outright: Claude
// Opus 4.7+, Sonnet 5 / 5.5, Haiku 5.5, Fable return HTTP 400 for a non-default temperature/top_p/top_k, and
// the OpenAI GPT-5+/o-series reasoning models accept only the default. Omitting the parameter is valid on
// every model, so the safe default for anything not on the allow-list is to omit it.

using System;
using System.Collections.Frozen;

namespace AgentOs.Modules.Llm;

/// <summary>Per-model request capabilities that change how the gateway shapes a call.</summary>
public static class ModelCapabilities
{
    // Canonical ids (see CostCalculator.CanonicalId) whose API accepts a caller-chosen temperature.
    private static readonly FrozenSet<string> AcceptsTemperature = FrozenSet.ToFrozenSet(
    [
        "claude-sonnet-4-6", "claude-sonnet-4-5", "claude-sonnet-4",
        "claude-opus-4-6", "claude-opus-4-5", "claude-opus-4-1", "claude-opus-4",
        "claude-haiku-4-5",
        "gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano",
        "gpt-4o", "gpt-4o-2024-05-13", "gpt-4o-mini",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="model"/> (or its dated snapshot) accepts <c>temperature</c>. Unknown models and
    /// deployment aliases return false — the parameter is then omitted, which every model accepts.
    /// </summary>
    public static bool AcceptsSamplingParameters(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && CostCalculator.CanonicalId(model) is { } id
        && AcceptsTemperature.Contains(id);

    /// <summary>The temperature to send for <paramref name="model"/>: the requested value when supported, else null (omitted).</summary>
    public static float? TemperatureFor(string? model, double requested) =>
        AcceptsSamplingParameters(model) ? (float)requested : null;
}
