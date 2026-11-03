// RBAC v2 (E1.2) — desktop app gating per role. The dock, Activities, Settings hub/search and the shell actions all
// filter through the same matrix; WindowHost refuses an app the role can't open. These pin what each role sees, so a
// gate can't silently drift from the endpoints it fronts.

using System.Linq;
using AgentOs.Modules.Tenants;
using AgentOs.SharedKernel.Identity;
using AgentOs.Web.Services;
using AgentOs.Web.Shell.Services;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class DesktopGatingTests
{
    private static string[] AppsFor(string role) =>
        [.. AppCatalog.VisibleAll(PermissionMatrix.For(role)).Select(a => a.Key)];

    private static readonly string[] AdminApps = ["users", "evidence", "tokens", "data", "audit", "policy", "prompts", "agents", "mcp"];
    private static readonly string[] EveryoneApps = ["home", "workflow", "board", "insights", "settings", "terminal", "files", "plugins", "system"];

    [Theory]
    [InlineData("viewer")]
    [InlineData("developer")]
    [InlineData("approver")]
    [InlineData("auditor")]
    [InlineData("admin")]
    [InlineData("member")]
    public void EveryRole_SeesTheWorkApps(string role)
        => EveryoneApps.ShouldBeSubsetOf(AppsFor(role));

    [Fact]
    public void Admin_SeesEveryApp()
        => AppsFor("admin").ShouldBe(AppCatalog.All.Select(a => a.Key), ignoreOrder: true);

    [Theory]
    [InlineData("viewer")]
    [InlineData("developer")]
    [InlineData("approver")]
    [InlineData("member")]
    public void NonAdminRoles_SeeNoAdminApp(string role)
        => AppsFor(role).Intersect(AdminApps).ShouldBeEmpty();

    [Fact]
    public void Auditor_SeesTheAuditApps_AndNothingElseAdmin()
        => AppsFor("auditor").Intersect(AdminApps).ShouldBe(["evidence", "audit"], ignoreOrder: true);

    [Fact]
    public void EveryAdminApp_DeclaresTheMatrixPermissionItNeeds()
    {
        foreach (var key in AdminApps)
        {
            AppCatalog.Find(key)!.Required.ShouldNotBe(Permission.None, key);
        }
    }

    [Fact]
    public void PluginAdminOnlyFlag_StillMeansAdmin()
    {
        var plugin = new DesktopApp("p", "P", "x", "c", "Plugins", AdminOnly: true);
        plugin.IsVisibleTo(PermissionMatrix.For("developer")).ShouldBeFalse();
        plugin.IsVisibleTo(PermissionMatrix.For("admin")).ShouldBeTrue();
        new DesktopApp("q", "Q", "x", "c", "Plugins").IsVisibleTo(Permission.None).ShouldBeTrue();
    }

    [Fact]
    public void Pinned_FollowsTheSameGate()
        => new AppCatalogRegistry().VisiblePinned(PermissionMatrix.For("viewer")).ShouldAllBe(a => a.IsVisibleTo(PermissionMatrix.For("viewer")));

    [Theory]
    [InlineData("invite", "admin", true)]
    [InlineData("invite", "auditor", false)]
    [InlineData("api token", "developer", false)]
    [InlineData("export", "admin", true)]
    [InlineData("export", "auditor", false)]
    [InlineData("add model key", "approver", false)]
    public void ShellActions_AreGatedByPermission(string query, string role, bool offered)
        => ShellActions.Find(query, PermissionMatrix.For(role)).Any().ShouldBe(offered);

    [Theory]
    [InlineData("audit log", "auditor", true)]
    [InlineData("audit log", "developer", false)]
    [InlineData("tool calls", "auditor", true)]
    [InlineData("invite", "auditor", false)]
    [InlineData("api token", "admin", true)]
    [InlineData("theme", "viewer", true)]
    public void SettingsSearch_OffersAPageOnlyToWhoHoldsItsPermission(string query, string role, bool offered)
        => SettingsSearch.Find(query, [], PermissionMatrix.For(role), isPlatformAdmin: false).Any().ShouldBe(offered);

    [Fact]
    public void Gate_PlatformPagesNeedPlatformAdmin_RegardlessOfTenantPermissions()
    {
        Gate.Platform.Allows(PermissionMatrix.All, isPlatformAdmin: false).ShouldBeFalse();
        Gate.Platform.Allows(Permission.None, isPlatformAdmin: true).ShouldBeTrue();
        Gate.Everyone.Allows(Permission.None, isPlatformAdmin: false).ShouldBeTrue();
    }

    // ── Member management rules (shared by the Users app and the HTTP endpoints) ──────────────────

    [Theory]
    [InlineData(new[] { "admin", "member" }, "admin")]
    [InlineData(new[] { "member" }, "member")]
    [InlineData(new[] { "viewer", "approver" }, "approver")]
    [InlineData(new[] { "developer", "auditor" }, "auditor")]
    [InlineData(new[] { "platform-admin", "default-roles-agentic" }, null)]
    public void MemberRoles_Primary_IsTheMostPrivilegedManagedRole(string[] roles, string? expected)
        => MemberRoles.Primary(roles).ShouldBe(expected);

    [Fact]
    public void MemberRoles_Sanitize_KeepsAllowedRolesOnly()
        => MemberRoles.Sanitize(["viewer", "platform-admin", "viewer", "member", "root"]).ShouldBe(["viewer", "member"]);

    [Fact]
    public void MemberRoles_Check_RefusesEmptyAndSelfDemotion()
    {
        MemberRoles.Check("me", "you", []).ShouldNotBeNull();
        MemberRoles.Check("me", "me", ["viewer"]).ShouldBe("You cannot remove your own admin role.");
        MemberRoles.Check("me", "me", ["admin"]).ShouldBeNull();
        MemberRoles.Check("me", "you", ["auditor"]).ShouldBeNull();
    }
}
