// Deep links into the Settings hub ("fix it" buttons, settings search). A target is "category[/sub-tab]" —
// e.g. "llm/keys" opens Settings → LLM & providers → Model keys. The hub reads the request when it opens, and
// again on every window-manager change, so a link works whether or not Settings is already open.

using AgentOs.Web.Shell.Services;

namespace AgentOs.Web.Services;

/// <summary>A sub-tab request handed from the hub to a category component. A fresh instance per link, so the
/// same target can be re-applied after the user navigated away inside the component (compared by reference).</summary>
public sealed class SettingsTabRequest(string tab)
{
    /// <summary>The sub-tab key, e.g. <c>keys</c>.</summary>
    public string Tab { get; } = tab;
}

/// <summary>Opens Settings at a page.</summary>
public static class SettingsLinks
{
    /// <summary>The hub's app key (also the launch-tab key the hub consumes).</summary>
    public const string AppKey = "settings";

    /// <summary>Settings → LLM &amp; providers → Model keys: where every "no usable key" message points.</summary>
    public const string ModelKeys = "llm/keys";

    /// <summary>Open (or focus) Settings on <paramref name="target"/>.</summary>
    public static void Open(WindowManagerService wm, string target)
    {
        System.ArgumentNullException.ThrowIfNull(wm);
        wm.RequestLaunchTab(AppKey, target);
        var app = AppCatalog.Find(AppKey);
        if (app is not null) { wm.OpenApp(app.Key, app.Title, app.Icon, app.W, app.H); }
    }

    /// <summary>Split a target into the hub category and the optional sub-tab.</summary>
    public static (string Category, string? Sub) Parse(string target)
    {
        var slash = target.IndexOf('/', System.StringComparison.Ordinal);
        return slash < 0 ? (target, null) : (target[..slash], target[(slash + 1)..]);
    }
}
