// Deep links into the Settings hub ("fix it" buttons, Activities search, shell actions). A target is a hub page
// key — e.g. "keys" opens Settings → AI → Model keys. The hub reads the request when it opens, and again on every
// window-manager change, so a link works whether or not Settings is already open. Pre-flattening targets
// ("llm/keys", "system/appearance", "llm") are still accepted (Normalize).

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using AgentOs.Web.Shell.Services;

namespace AgentOs.Web.Services;

/// <summary>Opens Settings at a page.</summary>
public static class SettingsLinks
{
    /// <summary>The hub's app key (also the launch-tab key the hub consumes).</summary>
    public const string AppKey = "settings";

    /// <summary>Settings → AI → Model keys: where every "no usable key" message points.</summary>
    public const string ModelKeys = "keys";

    /// <summary>Settings → AI → Autopilot: where Board › Autopilot points for how Autopilot runs.</summary>
    public const string Autopilot = "autopilot";

    /// <summary>Settings → Governance → Activity log → Tool calls.</summary>
    public const string ToolCalls = "evidence";

    /// <summary>The tool-call view filtered to the calls made on a member's computer, optionally for one task
    /// (<paramref name="sessionId"/>): what a Board task's activity panel links to.</summary>
    public static string MemberComputerToolCalls(Guid? sessionId = null) =>
        ToolCalls + "?origin=" + AgentOs.Domain.Tools.ToolEvidenceOrigins.MemberComputer
        + (sessionId is { } id ? "&session=" + id.ToString("D") : string.Empty);

    /// <summary>Split a target into its page key and, for the tool-call view, its origin and task filter. Anything
    /// unexpected in the query is dropped (only the member-computer origin and a Guid session id are kept).</summary>
    public static (string Key, string? Origin, string? SessionId) ParseToolCalls(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var q = target.IndexOf('?', StringComparison.Ordinal);
        if (q < 0) { return (target, null, null); }
        var key = target[..q];
        string? origin = null, session = null;
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) { continue; }
            var (name, value) = (pair[..eq], pair[(eq + 1)..]);
            if (name == "origin" && value == AgentOs.Domain.Tools.ToolEvidenceOrigins.MemberComputer) { origin = value; }
            else if (name == "session" && Guid.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var id)) { session = id.ToString("D"); }
        }
        return key == ToolCalls ? (key, origin, session) : (key, null, null);
    }

    // The old two-level targets → the flat page keys.
    private static readonly FrozenDictionary<string, string> Legacy = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["llm"] = "keys",
        ["llm/keys"] = "keys",
        ["llm/providers"] = "routing",
        ["llm/status"] = "diagnostics",
        ["llm/data"] = "diagnostics",
        ["llm/github"] = "github",
        ["system"] = "general",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Open (or focus) Settings on <paramref name="target"/>.</summary>
    public static void Open(WindowManagerService wm, string target)
    {
        ArgumentNullException.ThrowIfNull(wm);
        wm.RequestLaunchTab(AppKey, target);
        var app = AppCatalog.Find(AppKey);
        if (app is not null) { wm.OpenApp(app.Key, app.Title, app.Icon, app.W, app.H); }
    }

    /// <summary>The flat hub page key for a target (accepts the legacy "category/sub-tab" form).</summary>
    public static string Normalize(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var t = target.Trim();
        if (Legacy.TryGetValue(t, out var key)) { return key; }
        return t.StartsWith("system/", StringComparison.Ordinal) ? t["system/".Length..] : t;
    }
}
