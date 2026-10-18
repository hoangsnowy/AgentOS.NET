// UX phase 1 — Ctrl/⌘+K search finds actions (verbs), not only apps and settings; admin-only actions stay hidden
// from members, and every Settings action points at a real hub page.

using System.Linq;
using AgentOs.Web.Services;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class ShellActionsTests
{
    [Theory]
    [InlineData("run", ShellActions.Run)]
    [InlineData("dark", ShellActions.Theme)]
    [InlineData("invite", ShellActions.Invite)]
    [InlineData("add key", ShellActions.AddKey)]
    [InlineData("logout", ShellActions.SignOut)]
    public void Find_Admin_MatchesTitleOrKeywords(string query, string id)
        => ShellActions.Find(query, isAdmin: true)[0].Id.ShouldBe(id);

    [Fact]
    public void Find_Member_HidesAdminActions()
    {
        ShellActions.Find("invite", isAdmin: false).ShouldBeEmpty();
        ShellActions.Find("add key", isAdmin: false).ShouldBeEmpty();
        ShellActions.Find("theme", isAdmin: false).ShouldHaveSingleItem().Id.ShouldBe(ShellActions.Theme);
        ShellActions.Find(" ", isAdmin: true).ShouldBeEmpty();
    }

    [Fact]
    public void SettingsTargets_AreHubCategories()
    {
        string[] hub = ["llm", "users", "tokens", "data"];
        new[] { ShellActions.AddKey, ShellActions.Invite, ShellActions.Token, ShellActions.Export }
            .Select(id => SettingsLinks.Parse(ShellActions.SettingsTarget(id)!).Category)
            .ShouldAllBe(c => hub.Contains(c));
        ShellActions.SettingsTarget(ShellActions.Run).ShouldBeNull();
    }
}
