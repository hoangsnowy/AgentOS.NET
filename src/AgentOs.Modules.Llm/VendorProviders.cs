// Bring-your-own-key vendors on the OpenAI wire protocol (competitor parity: OpenAI, Gemini, xAI next to Claude
// and Azure OpenAI). Endpoints are FIXED here — never tenant input — so a tenant key setting can't become an
// SSRF vector (the free-form OpenAI-compatible provider stays platform-only). Keys and default models are
// tenant-scoped settings: a tenant brings its own key; the platform seed is the fallback.
//   Sources (checked 2026-10-18): platform.openai.com/docs, ai.google.dev/gemini-api/docs/openai,
//   docs.x.ai/docs/models.

using System;
using System.Collections.Generic;

namespace AgentOs.Modules.Llm;

/// <summary>One BYOK vendor.</summary>
/// <param name="Name">Canonical provider name (keyed ILlmClient + Settings choice).</param>
/// <param name="Endpoint">Fixed OpenAI-compatible base URL.</param>
/// <param name="KeySetting">Tenant-scoped secret setting holding the API key.</param>
/// <param name="ModelSetting">Tenant-scoped setting holding the default model.</param>
/// <param name="DefaultModel">Built-in default model id.</param>
/// <param name="ModelPrefixes">Model ids this vendor serves (anything else → the default model).</param>
public sealed record VendorProvider(string Name, string Endpoint, string KeySetting, string ModelSetting, string DefaultModel, IReadOnlyList<string> ModelPrefixes)
{
    /// <summary>The model to request: the profile's model when this vendor serves it, else the configured default.</summary>
    public string ResolveModel(string? requested, string? configured)
    {
        var fallback = string.IsNullOrWhiteSpace(configured) ? DefaultModel : configured.Trim();
        if (string.IsNullOrWhiteSpace(requested)) { return fallback; }
        foreach (var prefix in ModelPrefixes)
        {
            if (requested.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return requested.Trim(); }
        }
        return fallback;
    }
}

/// <summary>The BYOK vendor catalogue.</summary>
public static class VendorProviders
{
    /// <summary>OpenAI (api.openai.com). "OpenAI" now means this provider, not Azure.</summary>
    public static VendorProvider OpenAi { get; } = new(
        "OpenAI", "https://api.openai.com/v1", "Llm:OpenAi:ApiKey", "Llm:OpenAi:Model", "gpt-5.4", ["gpt-", "o1", "o3", "o4"]);

    /// <summary>Google Gemini through its OpenAI-compatible endpoint (beta at Google).</summary>
    public static VendorProvider Gemini { get; } = new(
        "Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", "Llm:Gemini:ApiKey", "Llm:Gemini:Model", "gemini-3.8-flash", ["gemini-"]);

    /// <summary>xAI Grok.</summary>
    public static VendorProvider Xai { get; } = new(
        "xAI", "https://api.x.ai/v1", "Llm:Xai:ApiKey", "Llm:Xai:Model", "grok-4.7", ["grok-"]);

    /// <summary>All of them, in display order.</summary>
    public static IReadOnlyList<VendorProvider> All { get; } = [OpenAi, Gemini, Xai];
}
