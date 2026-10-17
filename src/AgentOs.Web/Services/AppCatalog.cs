// AgentOs.Web/Services/AppCatalog.cs
// Single source of truth for the desktop's launchable apps. The Start menu, dock and
// desktop icons all read from here so a new app is registered in exactly one place.

using System.Collections.Generic;
using System.Linq;
using AgentOs.Web.Components.Pages;
using AgentOs.Web.Shell.Services;

namespace AgentOs.Web.Services;

/// <summary>The catalog of apps the AgentOS shell can launch — built-ins plus any registered by plugins.</summary>
public static class AppCatalog
{
    // Six apps are Pinned (the GNOME Dash + pinned grid): Insights · Workflow · Board · Settings ·
    // Terminal · Files. Everything else is unpinned — still launchable via Activities search / deep
    // links, and the admin/system surfaces are collected behind the Settings hub. Pinned defaults to
    // true on the DesktopApp record, so unpinned apps MUST say Pinned: false explicitly.
    private static readonly List<DesktopApp> _builtIn = new()
    {
        // Insights is the landing dashboard (runs · agent utilisation · cost analytics + budget); running
        // the pipeline lives in the Workflow app, so there is no separate "Agents" run window. The old
        // standalone Overview + Cost admin windows are folded in here.
        new("insights", "Insights", "house", "Runs, agent utilisation and LLM cost — analytics + budget", "Agents", 1180, 760, Pinned: true, ComponentType: typeof(InsightsApp), Color: "#3584e4"),
        new("workflow", "Workflow", "graph", "Build & run the agent pipeline as a visual graph", "Agents", 1320, 840, Pinned: true, ComponentType: typeof(OrchestrationStudio), Color: "#9141ac"),
        new("board",    "Board",    "git-pull-request", "Boards → tickets → AI sessions → PRs", "Agents", 1000, 660, Pinned: true, ComponentType: typeof(BoardApp), Color: "#2ec27e"),
        new("settings", "Settings", "gear",  "LLM keys, governance, users, system",   "System", 1060, 700, Pinned: true, ComponentType: typeof(SettingsHub), Color: "#5e5c64"),
        new("terminal", "Terminal", "terminal", "Live read-only tail of the runner's session feed", "Agents", 780, 520, Pinned: true, ComponentType: typeof(TerminalApp), Color: "#2b2b2e"),
        new("files",    "Files",    "folder", "Browse a connected repo's files (read-only)", "Agents", 880, 600, Pinned: true, ComponentType: typeof(FilesApp), Color: "#4a86d8"),
        // ── unpinned: reachable via the Settings hub, Activities search, or in-app deep links ──
        // (Workspaces + Sessions were standalone admin windows fully subsumed by Board → deleted.)
        new("users",    "Users",    "user",  "Manage tenant members + roles", "Admin", 880, 600, Pinned: false, AdminOnly: true, ComponentType: typeof(UsersApp), Color: "#e5a50a"),
        new("evidence", "Evidence", "lock",  "Tool-invocation audit trail",   "Admin", 960, 620, Pinned: false, AdminOnly: true, ComponentType: typeof(EvidenceApp), Color: "#c01c28"),
        new("data",     "Data", "floppy-disk", "Export this workspace; platform admins purge tenants", "Admin", 900, 600, Pinned: false, AdminOnly: true, ComponentType: typeof(DataApp), Color: "#26a269"),
        new("audit",    "Audit log", "clock-counter", "Who changed what — settings, members, sign-in", "Admin", 980, 640, Pinned: false, AdminOnly: true, ComponentType: typeof(AuditLogApp), Color: "#865e3c"),
        new("policy",   "Policy",   "lock",  "Per-tenant tool allowlist",     "Admin", 900, 620, Pinned: false, AdminOnly: true, ComponentType: typeof(PolicyApp), Color: "#613583"),
        new("prompts",  "Prompts",  "lightning", "Tune the agents' prompts",   "Admin", 1000, 680, Pinned: false, AdminOnly: true, ComponentType: typeof(PromptsApp), Color: "#c061cb"),
        new("agents",   "Agent profiles", "diamond", "Model + limits per agent",  "Admin", 1100, 620, Pinned: false, AdminOnly: true, ComponentType: typeof(AgentProfilesApp), Color: "#1c71d8"),
        new("plugins",  "Plugins",  "squares-stack", "Installed extensions",  "System", 860, 600, Pinned: false, ComponentType: typeof(PluginsApp), Color: "#5e5c64"),
        new("mcp",      "MCP",      "arrow-square-out", "Upstream MCP tool servers", "System", 860, 600, Pinned: false, AdminOnly: true, ComponentType: typeof(McpApp), Color: "#1a5fb4"),
        new("system",   "System",   "wrench","OS appearance, themes, about",  "System", 760, 600, Pinned: false, ComponentType: typeof(SystemApp), Color: "#3d3846"),
    };

    // Plugin-contributed apps, appended once at host startup (before any request is served).
    private static readonly List<DesktopApp> _plugin = new();

    /// <summary>Register plugin-contributed desktop apps (idempotent by key). Call once at startup.</summary>
    public static void RegisterPluginApps(IEnumerable<DesktopApp> apps)
    {
        foreach (var app in apps.Where(app =>
            !_builtIn.Any(a => a.Key == app.Key) && !_plugin.Any(a => a.Key == app.Key)))
        {
            _plugin.Add(app);
        }
    }

    /// <summary>All registered apps, in display order (built-ins first, then plugin apps).</summary>
    public static IReadOnlyList<DesktopApp> All => _builtIn.Concat(_plugin).ToList();

    /// <summary>Apps that appear in the dock and the pinned grid.</summary>
    public static IReadOnlyList<DesktopApp> Pinned => All.Where(a => a.Pinned).ToList();

    /// <summary>Resolve an app by key, or <c>null</c> if unknown.</summary>
    public static DesktopApp? Find(string key) =>
        All.FirstOrDefault(a => a.Key == key);

    /// <summary>All apps visible to a user with the given admin status (filters out <see cref="DesktopApp.AdminOnly"/>).</summary>
    public static IEnumerable<DesktopApp> VisibleAll(bool isAdmin) =>
        All.Where(a => isAdmin || !a.AdminOnly);

    /// <summary>Pinned apps visible to a user with the given admin status.</summary>
    public static IEnumerable<DesktopApp> VisiblePinned(bool isAdmin) =>
        Pinned.Where(a => isAdmin || !a.AdminOnly);
}
