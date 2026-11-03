// AgentOs.Web.Shell/Services/DesktopApp.cs
// The shell's app descriptor — the data the windowing chrome (dock, taskbar, WindowHost) reads to launch
// and render a desktop app. The host owns the actual catalog (built-in component types + plugin apps) and
// exposes it through IAppRegistry; the shell only needs this record shape.

using AgentOs.SharedKernel.Identity;

namespace AgentOs.Web.Shell.Services;

/// <summary>A launchable desktop application.</summary>
/// <param name="Key">Stable id used by <see cref="WindowManagerService"/> and recents.</param>
/// <param name="Title">Window + launcher title.</param>
/// <param name="Icon">Icon name (see <c>Icon.razor</c> map).</param>
/// <param name="Caption">One-line description shown in the launcher.</param>
/// <param name="Category">Grouping bucket in the Start menu.</param>
/// <param name="W">Default window width.</param>
/// <param name="H">Default window height.</param>
/// <param name="Pinned">Whether the app appears in the dock + pinned grid.</param>
/// <param name="AdminOnly">When true the app is only shown to tenant admins (kept for plugins; built-ins use
/// <paramref name="Requires"/>). Treated as requiring <see cref="Permission.ManageSettings"/>.</param>
/// <param name="ComponentType">The Blazor component the (app-agnostic) WindowHost renders via
/// <c>DynamicComponent</c> — set for every app, built-in and plugin alike. The shell never names an app.</param>
/// <param name="Color">Launcher tile colour.</param>
/// <param name="Requires">RBAC v2: the permission (PermissionMatrix) a user needs to see and open the app;
/// <see cref="Permission.None"/> = every signed-in user.</param>
public sealed record DesktopApp(
    string Key,
    string Title,
    string Icon,
    string Caption,
    string Category,
    int W = 920,
    int H = 620,
    bool Pinned = true,
    bool AdminOnly = false,
    System.Type? ComponentType = null,
    string Color = "#3584e4",
    Permission Requires = Permission.None)
{
    /// <summary>The permission this app needs, folding the legacy <see cref="AdminOnly"/> flag in.</summary>
    public Permission Required => Requires != Permission.None ? Requires : AdminOnly ? Permission.ManageSettings : Permission.None;

    /// <summary>True when a user granted <paramref name="granted"/> may see and open the app.</summary>
    public bool IsVisibleTo(Permission granted) => Required == Permission.None || granted.Has(Required);
}
