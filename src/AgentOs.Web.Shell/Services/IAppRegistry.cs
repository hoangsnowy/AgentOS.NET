// The shell's view of the host's app catalog. The windowing chrome (WindowHost) needs to turn a window's
// app key into the Blazor component to render — but the shell must not know any specific app, and the
// host's AppCatalog (with its built-in component types) lives in the host. So the shell depends on this
// interface; the host implements it. This is the seam that lets the chrome live in AgentOs.Web.Shell.

namespace AgentOs.Web.Shell.Services;

/// <summary>Resolves a desktop app key to the component the <c>WindowHost</c> renders, plus the catalog
/// lookups the dock + taskbar need. Host-implemented.</summary>
public interface IAppRegistry
{
    /// <summary>The Blazor component type registered for <paramref name="appKey"/>, or <c>null</c> if unknown
    /// (the host renders the "Unknown app" placeholder).</summary>
    System.Type? ResolveComponent(string appKey);

    /// <summary>The app registered for <paramref name="appKey"/>, or <c>null</c> if unknown.</summary>
    DesktopApp? Find(string appKey);

    /// <summary>Apps that appear in the dock and the pinned grid.</summary>
    System.Collections.Generic.IReadOnlyList<DesktopApp> Pinned { get; }

    /// <summary>Pinned apps visible to a user granted <paramref name="granted"/> (RBAC v2 — see <see cref="DesktopApp.IsVisibleTo"/>).</summary>
    System.Collections.Generic.IReadOnlyList<DesktopApp> VisiblePinned(AgentOs.SharedKernel.Identity.Permission granted);
}
