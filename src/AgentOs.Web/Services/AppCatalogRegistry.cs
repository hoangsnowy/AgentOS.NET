// Host implementation of the shell's IAppRegistry seam: delegates to the static AppCatalog (which owns
// the built-in app list + their host component types, plus plugin-contributed apps). Lets the RCL's
// WindowHost resolve a window's component without referencing the host's AppCatalog directly.

using System.Collections.Generic;
using System.Linq;
using AgentOs.Web.Shell.Services;

namespace AgentOs.Web.Services;

/// <summary>Adapts <see cref="AppCatalog"/> to the shell's <see cref="IAppRegistry"/> seam.</summary>
internal sealed class AppCatalogRegistry : IAppRegistry
{
    public System.Type? ResolveComponent(string appKey) => AppCatalog.Find(appKey)?.ComponentType;

    public DesktopApp? Find(string appKey) => AppCatalog.Find(appKey);

    public IReadOnlyList<DesktopApp> Pinned => AppCatalog.Pinned;

    public IReadOnlyList<DesktopApp> VisiblePinned(AgentOs.SharedKernel.Identity.Permission granted) => AppCatalog.VisiblePinned(granted).ToList();
}
