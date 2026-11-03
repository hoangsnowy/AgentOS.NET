// UX simplification (docs/plans/ux-simplification.md, phase 0): settings are findable from Activities search.
// Before, search matched app names only, so "gemini key" or "dark mode" found nothing and users had to know which
// of ~22 nested settings screens held a field. This index has every Settings page (with the words people use
// for it) plus every setting descriptor's label, each mapped to the page that edits it. A hit deep-links to
// the page through SettingsLinks. Permission-gated (RBAC v2 matrix): a page shows only to who holds its permission
// (e.g. Activity log → ViewAudit, so auditors find it), only platform admins see Platform.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.Llm;
using AgentOs.SharedKernel.Identity;

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
    private sealed record Page(string Target, string Title, string Path, string Icon, string Keywords, Gate Gate);

    private const string Ai = "Settings › AI";
    private const string Conn = "Settings › Connections";
    private const string Gov = "Settings › Governance";
    private const string People = "Settings › People & data";
    private const string Personal = "Settings › Personal";
    private const string Sys = "Settings › System";

    // Rail order (SettingsHub.Items). Keywords are the words people type for the page, not only the words on it.
    private static readonly Page[] Pages =
    [
        new("keys", "Model keys", Ai, "key", "api key apikey secret byok anthropic claude azure openai gpt gemini google xai grok endpoint", Gate.Of(Permission.ManageSettings)),
        new("routing", "Routing", Ai, "lightning", "force provider default provider routing test connection offline fallback remoteagent remote agent paired tool output budget tokens", Gate.Of(Permission.ManageSettings)),
        new("agents", "Agent profiles", Ai, "diamond", "agent model per agent temperature max tokens output qa loop iterations cap profile coding testing requirement orchestrator", Gate.Of(Permission.ManageSettings)),
        new("prompts", "Prompts", Ai, "file", "prompt system prompt workspace rules instructions version history rollback", Gate.Of(Permission.ManageSettings)),
        new("autopilot", "Autopilot", Ai, "play", "autopilot orchestrator decided by rules maf workflow step by step tasks at once parallel acceptance tests fix rounds retries checks timeout poll merge plan limit files attachments ticket files upload size comments discussion maintainer decisions", Gate.Of(Permission.ManageSettings)),
        new("github", "GitHub", Conn, "git-pull-request", "github token pat pull request repo owner pipeline", Gate.Of(Permission.ManageSettings)),
        new("mcp", "MCP servers", Conn, "arrow-square-out", "mcp model context protocol tool servers upstream", Gate.Of(Permission.ManageSettings)),
        new("plugins", "Plugins", Conn, "squares-stack", "plugins extensions add-ons", Gate.Everyone),
        new("policy", "Tool policy", Gov, "shield", "tool allowlist policy permissions governance", Gate.Of(Permission.ManageSettings)),
        new("evidence", "Tool calls", Gov + " › Activity log", "scroll", "evidence tool calls invocations trail governance", Gate.Of(Permission.ViewAudit)),
        new("audit", "Activity log", Gov, "clock-counter", "audit log activity who changed history sign-in login members csv", Gate.Of(Permission.ViewAudit)),
        new("users", "Users", People, "user", "users members invite invitation roles people team", Gate.Of(Permission.ManageMembers)),
        new("tokens", "API tokens", People, "tag", "api token service account ci pipeline script machine bearer mcp client", Gate.Of(Permission.ManageApiTokens)),
        new("data", "Data", People, "floppy-disk", "export purge delete tenant workspace data download gdpr", Gate.Of(Permission.ExportData)),
        new("general", "General", Personal, "gear", "general preferences", Gate.Everyone),
        new("appearance", "Appearance", Personal, "palette", "appearance theme dark light mode wallpaper accent glass motion animation", Gate.Everyone),
        new("datetime", "Date & time", Personal, "clock", "date time clock time zone 24-hour", Gate.Everyone),
        new("notifs", "Notifications", Personal, "bell", "notifications toasts alerts", Gate.Everyone),
        new("session", "Session", Personal, "power", "session sign out logout", Gate.Everyone),
        new("about", "About", Sys, "info", "about version build health services status", Gate.Everyone),
        new("diagnostics", "Diagnostics", Sys, "circle-fill", "status diagnostics effective configuration which provider storage persistence database postgres stateless", Gate.Of(Permission.ManageSettings)),
        new("platform", "Platform", Sys, "server", "platform safety run limits concurrency sandbox docker openai-compatible self-hosted ollama vllm foundry", Gate.Platform),
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
        { Category: "Tools" } => "routing", // Tools:MaxOutputTokens sits with the tenant's routing settings
        { Category: AgentOs.Modules.Pipeline.Agents.AutopilotSettings.Category } => SettingsLinks.Autopilot,
        _ => null,
    };

    /// <summary><see cref="Find(string, IEnumerable{SettingDescriptor}, Permission, bool, int)"/> for an admin (every
    /// tenant permission) or a non-admin (<see cref="Permission.ViewWork"/> only).</summary>
    public static IReadOnlyList<SettingsHit> Find(
        string query, IEnumerable<SettingDescriptor> descriptors, bool isAdmin, bool isPlatformAdmin, int max = 6)
        => Find(query, descriptors, isAdmin ? PermissionMatrix.All : Permission.ViewWork, isPlatformAdmin, max);

    /// <summary>Pages first (best match first), then individual settings; at most <paramref name="max"/>. A page is
    /// offered only when <paramref name="granted"/> holds its permission (RBAC v2) — the same gate the hub uses.</summary>
    public static IReadOnlyList<SettingsHit> Find(
        string query, IEnumerable<SettingDescriptor> descriptors, Permission granted, bool isPlatformAdmin, int max = 6)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) { return []; }
        bool Allowed(Page p) => p.Gate.Allows(granted, isPlatformAdmin);
        // Every term must appear in one of the fields. Terms never contain a space, so matching each field on its own
        // equals matching "first second" — without building that string per page and per descriptor on every keystroke.
        bool Matches(string first, string? second = null)
        {
            foreach (var t in terms)
            {
                if (!first.Contains(t, StringComparison.OrdinalIgnoreCase)
                    && (second is null || !second.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            return true;
        }

        var hits = Pages
            .Where(p => Allowed(p) && Matches(p.Title, p.Keywords))
            .OrderBy(p => p.Title.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase) ? 0 : Matches(p.Title) ? 1 : 2)
            .Select(p => new SettingsHit(p.Title, p.Path, p.Target, p.Icon))
            .ToList();

        var seen = new HashSet<string>(hits.Select(h => h.Title), StringComparer.OrdinalIgnoreCase);
        foreach (var d in descriptors)
        {
            if (hits.Count >= max) { break; }
            if (TargetOf(d) is not { } target || !ByTarget.TryGetValue(target, out var page) || !Allowed(page)) { continue; }
            if (!Matches(d.Label, d.Key) || !seen.Add(d.Label)) { continue; }
            hits.Add(new SettingsHit(d.Label, page.Path + " › " + page.Title, target, page.Icon));
        }
        return hits.Count > max ? hits.GetRange(0, max) : hits;
    }
}

/// <summary>Who may open a Settings page: everyone, holders of one permission (RBAC v2), or platform admins.</summary>
/// <param name="Requires">The permission the page needs; <see cref="Permission.None"/> = everyone.</param>
/// <param name="PlatformOnly">Only platform admins (platform-wide settings).</param>
public readonly record struct Gate(Permission Requires, bool PlatformOnly = false)
{
    /// <summary>Open to every signed-in user.</summary>
    public static Gate Everyone => default;

    /// <summary>Platform admins only.</summary>
    public static Gate Platform => new(Permission.None, PlatformOnly: true);

    /// <summary>Holders of <paramref name="permission"/>.</summary>
    public static Gate Of(Permission permission) => new(permission);

    /// <summary>True when a user with <paramref name="granted"/> (and platform-admin status) may open the page.</summary>
    public bool Allows(Permission granted, bool isPlatformAdmin) =>
        PlatformOnly ? isPlatformAdmin : Requires == Permission.None || granted.Has(Requires);
}
