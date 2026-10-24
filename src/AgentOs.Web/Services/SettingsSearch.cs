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
/// <param name="Path">Where it lives, e.g. <c>Settings › AI</c>.</param>
/// <param name="Target">The hub page key for <see cref="SettingsLinks.Open"/>.</param>
/// <param name="Icon">The page's icon.</param>
public sealed record SettingsHit(string Title, string Path, string Target, string Icon);

/// <summary>Search over Settings pages + setting descriptors.</summary>
public static class SettingsSearch
{
    private enum Access { Everyone, Admin, Platform }

    private sealed record Page(string Target, string Title, string Path, string Icon, string Keywords, Access Access);

    private const string Ai = "Settings › AI";
    private const string Conn = "Settings › Connections";
    private const string Gov = "Settings › Governance";
    private const string People = "Settings › People & data";
    private const string Personal = "Settings › Personal";
    private const string Sys = "Settings › System";

    // Rail order (SettingsHub.Items). Keywords are the words people type for the page, not only the words on it.
    private static readonly Page[] Pages =
    [
        new("keys", "Model keys", Ai, "key", "api key apikey secret byok anthropic claude azure openai gpt gemini google xai grok endpoint", Access.Admin),
        new("routing", "Routing", Ai, "lightning", "force provider default provider routing test connection offline fallback remoteagent remote agent paired", Access.Admin),
        new("agents", "Agent profiles", Ai, "diamond", "agent model per agent temperature max tokens output qa loop iterations cap profile coding testing requirement orchestrator", Access.Admin),
        new("prompts", "Prompts", Ai, "file", "prompt system prompt workspace rules instructions version history rollback", Access.Admin),
        new("autopilot", "Autopilot", Ai, "play", "autopilot orchestrator decided by rules maf workflow step by step tasks at once parallel acceptance tests fix rounds retries checks timeout poll merge plan limit files attachments ticket files upload size", Access.Admin),
        new("github", "GitHub", Conn, "git-pull-request", "github token pat pull request repo owner pipeline", Access.Admin),
        new("mcp", "MCP servers", Conn, "arrow-square-out", "mcp model context protocol tool servers upstream", Access.Admin),
        new("plugins", "Plugins", Conn, "squares-stack", "plugins extensions add-ons", Access.Everyone),
        new("policy", "Tool policy", Gov, "shield", "tool allowlist policy permissions governance", Access.Admin),
        new("evidence", "Tool calls", Gov + " › Activity log", "scroll", "evidence tool calls invocations trail governance", Access.Admin),
        new("audit", "Activity log", Gov, "clock-counter", "audit log activity who changed history sign-in login members csv", Access.Admin),
        new("users", "Users", People, "user", "users members invite invitation roles people team", Access.Admin),
        new("tokens", "API tokens", People, "tag", "api token service account ci pipeline script machine bearer mcp client", Access.Admin),
        new("data", "Data", People, "floppy-disk", "export purge delete tenant workspace data download gdpr", Access.Admin),
        new("general", "General", Personal, "gear", "general preferences", Access.Everyone),
        new("appearance", "Appearance", Personal, "palette", "appearance theme dark light mode wallpaper accent glass motion animation", Access.Everyone),
        new("datetime", "Date & time", Personal, "clock", "date time clock time zone 24-hour", Access.Everyone),
        new("notifs", "Notifications", Personal, "bell", "notifications toasts alerts", Access.Everyone),
        new("session", "Session", Personal, "power", "session sign out logout", Access.Everyone),
        new("about", "About", Sys, "info", "about version build health services status", Access.Everyone),
        new("diagnostics", "Diagnostics", Sys, "circle-fill", "status diagnostics effective configuration which provider storage persistence database postgres stateless", Access.Admin),
        new("platform", "Platform", Sys, "server", "platform safety run limits concurrency sandbox docker openai-compatible self-hosted ollama vllm foundry", Access.Platform),
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
        { Category: "LLM providers" } => ModelKeySettings.Contains(d.Key) ? "keys" : "routing",
        { Category: "Build sandbox" or "Platform" } => "platform",
        { Category: AgentOs.Modules.Pipeline.Agents.AutopilotSettings.Category } => SettingsLinks.Autopilot,
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
