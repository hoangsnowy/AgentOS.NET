// UX simplification (docs/plans/ux-simplification.md, phase 0): settings are findable from Activities search.
// Before, search matched app names only, so "gemini key" or "dark mode" found nothing and users had to know which
// of ~22 nested settings screens held a field. This index has every Settings page (with the words people use
// for it) plus every setting descriptor's label, each mapped to the page that edits it. A hit deep-links to
// the page through SettingsLinks. Role-gated: members never see admin pages, only platform admins see Platform.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.Llm;

namespace AgentOs.Web.Services;

/// <summary>One settings search hit.</summary>
/// <param name="Title">What matched (a page or a setting label).</param>
/// <param name="Path">Where it lives, e.g. <c>Settings › LLM &amp; providers › Model keys</c>.</param>
/// <param name="Target">The deep-link target (<c>hub-category[/sub-tab]</c>) for <see cref="SettingsLinks.Open"/>.</param>
/// <param name="Icon">The page's icon.</param>
public sealed record SettingsHit(string Title, string Path, string Target, string Icon);

/// <summary>Search over Settings pages + setting descriptors.</summary>
public static class SettingsSearch
{
    private enum Access { Everyone, Admin, Platform }

    private sealed record Page(string Target, string Title, string Path, string Icon, string Keywords, Access Access);

    private const string Llm = "Settings › LLM & providers";
    private const string Sys = "Settings › System";

    // Rail order. Keywords are the words people type for the page, not only the words on it.
    private static readonly Page[] Pages =
    [
        new("llm/keys", "Model keys", Llm, "key", "api key apikey secret byok anthropic claude azure openai gpt gemini google xai grok endpoint", Access.Admin),
        new("llm/providers", "Providers & routing", Llm, "lightning", "force provider default provider routing test connection offline fallback remoteagent remote agent paired", Access.Admin),
        new("llm/status", "LLM status", Llm, "circle-fill", "status effective configuration which provider", Access.Admin),
        new("llm/github", "GitHub (pipeline PRs)", Llm, "arrow-square-out", "github token pat pull request repo owner", Access.Admin),
        new("llm/data", "Storage", Llm, "floppy-disk", "storage persistence database postgres stateless", Access.Admin),
        new("agents", "Agent profiles", "Settings", "diamond", "agent model per agent temperature max tokens output qa loop iterations cap profile coding testing requirement orchestrator", Access.Admin),
        new("prompts", "Prompts", "Settings", "lightning", "prompt system prompt workspace rules instructions version history rollback", Access.Admin),
        new("policy", "Tool policy", "Settings", "shield", "tool allowlist policy permissions governance", Access.Admin),
        new("evidence", "Evidence", "Settings", "scroll", "evidence tool calls invocations trail governance", Access.Admin),
        new("audit", "Audit log", "Settings", "clock-counter", "audit log who changed history sign-in login members csv", Access.Admin),
        new("data", "Data", "Settings", "floppy-disk", "export purge delete tenant workspace data download gdpr", Access.Admin),
        new("tokens", "API tokens", "Settings", "tag", "api token service account ci pipeline script machine bearer mcp client", Access.Admin),
        new("users", "Users", "Settings", "user", "users members invite invitation roles people team", Access.Admin),
        new("mcp", "MCP servers", "Settings", "arrow-square-out", "mcp model context protocol tool servers upstream", Access.Admin),
        new("plugins", "Plugins", "Settings", "squares-stack", "plugins extensions add-ons", Access.Everyone),
        new("platform", "Platform", "Settings", "server", "platform safety run limits concurrency sandbox docker openai-compatible self-hosted ollama vllm foundry", Access.Platform),
        new("system/general", "General", Sys, "gear", "general preferences", Access.Everyone),
        new("system/appearance", "Appearance", Sys, "palette", "appearance theme dark light mode wallpaper accent glass motion animation", Access.Everyone),
        new("system/datetime", "Date & time", Sys, "clock", "date time clock time zone 24-hour", Access.Everyone),
        new("system/notifs", "Notifications", Sys, "bell", "notifications toasts alerts", Access.Everyone),
        new("system/about", "About", Sys, "info", "about version build health services status", Access.Everyone),
        new("system/session", "Session", Sys, "power", "session sign out logout", Access.Everyone),
    ];

    private static readonly Dictionary<string, Page> ByTarget = Pages.ToDictionary(p => p.Target, StringComparer.Ordinal);

    // Model keys holds every provider key + endpoint and the BYOK vendors' default models; the rest of the LLM
    // category is on Providers & routing.
    private static readonly HashSet<string> ModelKeySettings = new(
        VendorProviders.All.SelectMany(v => new[] { v.KeySetting, v.ModelSetting })
            .Concat([AppConfigKeys.LlmClaudeApiKey, AppConfigKeys.LlmAzureApiKey, AppConfigKeys.LlmAzureEndpoint]),
        StringComparer.OrdinalIgnoreCase);

    // Settings with no editor anywhere yet (key pools: UX phase 1 "Advanced") — never offered as a hit.
    private static readonly HashSet<string> NoEditor = new([AppConfigKeys.LlmClaudeApiKeys, AppConfigKeys.LlmAzureApiKeys], StringComparer.OrdinalIgnoreCase);

    /// <summary>The page that edits <paramref name="d"/>, or null when it has no settings page.</summary>
    internal static string? TargetOf(SettingDescriptor d) => d switch
    {
        _ when NoEditor.Contains(d.Key) => null,
        { Scope: SettingScope.Platform } => "platform",
        { Category: "Agent profiles" or "Pipeline" } => "agents",
        { Category: "LLM providers" } => ModelKeySettings.Contains(d.Key) ? "llm/keys" : "llm/providers",
        { Category: "Build sandbox" or "Platform" } => "platform",
        _ => null,
    };

    /// <summary>Pages first (best match first), then individual settings; at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<SettingsHit> Find(
        string query, IEnumerable<SettingDescriptor> descriptors, bool isAdmin, bool isPlatformAdmin, int max = 6)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) { return []; }
        bool Allowed(Page p) => p.Access switch
        {
            Access.Admin => isAdmin,
            Access.Platform => isPlatformAdmin,
            _ => true,
        };
        bool Matches(string haystack) => terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));

        var hits = Pages
            .Where(p => Allowed(p) && Matches(p.Title + " " + p.Keywords))
            .OrderBy(p => p.Title.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase) ? 0 : Matches(p.Title) ? 1 : 2)
            .Select(p => new SettingsHit(p.Title, p.Path, p.Target, p.Icon))
            .ToList();

        var seen = new HashSet<string>(hits.Select(h => h.Title), StringComparer.OrdinalIgnoreCase);
        foreach (var d in descriptors)
        {
            if (hits.Count >= max) { break; }
            if (TargetOf(d) is not { } target || !ByTarget.TryGetValue(target, out var page) || !Allowed(page)) { continue; }
            if (!Matches(d.Label + " " + d.Key) || !seen.Add(d.Label)) { continue; }
            hits.Add(new SettingsHit(d.Label, page.Path + " › " + page.Title, target, page.Icon));
        }
        return hits.Count > max ? hits.GetRange(0, max) : hits;
    }
}
