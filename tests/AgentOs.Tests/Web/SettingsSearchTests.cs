// UX phase 0 — settings are findable from Activities search and deep-linkable. Search covers every Settings page
// (with the words people type) and every setting label, maps each to the page that edits it, and never shows a
// member an admin page or anyone but a platform admin the Platform page.

using System.Linq;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Web.Services;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class SettingsSearchTests
{
    private static readonly SettingDescriptor[] Descriptors =
        [.. new LlmSettings().Describe(), .. new AgentSettings().Describe()];

    [Theory]
    [InlineData("gemini key", "keys")]
    [InlineData("anthropic", "keys")]
    [InlineData("force provider", "routing")]
    [InlineData("dark mode", "appearance")]
    [InlineData("postgres", "diagnostics")]
    [InlineData("who changed", "audit")]
    [InlineData("service account", "tokens")]
    [InlineData("qa loop", "agents")]
    public void Find_Admin_TopHitIsThePageThatEditsIt(string query, string target)
        => SettingsSearch.Find(query, Descriptors, isAdmin: true, isPlatformAdmin: false)[0].Target.ShouldBe(target);

    [Fact]
    public void Find_Member_NeverSeesAdminPages()
    {
        SettingsSearch.Find("key", Descriptors, isAdmin: false, isPlatformAdmin: false).ShouldBeEmpty();
        SettingsSearch.Find("theme", Descriptors, isAdmin: false, isPlatformAdmin: false)
            .ShouldHaveSingleItem().Target.ShouldBe("appearance");
    }

    [Fact]
    public void Find_PlatformPageAndPlatformSettings_OnlyForPlatformAdmins()
    {
        SettingsSearch.Find("ollama", Descriptors, isAdmin: true, isPlatformAdmin: false).ShouldBeEmpty();
        SettingsSearch.Find("ollama", Descriptors, isAdmin: true, isPlatformAdmin: true)
            .ShouldAllBe(h => h.Target == "platform");
    }

    [Fact]
    public void Find_SettingLabels_DeepLinkToTheirPage_WithoutDuplicates()
    {
        var hits = SettingsSearch.Find("xai", Descriptors, isAdmin: true, isPlatformAdmin: false, max: 10);
        hits.ShouldContain(h => h.Title == "xAI API key" && h.Target == "keys");
        hits.Select(h => h.Title).ShouldBeUnique();
        SettingsSearch.Find("", Descriptors, true, true).ShouldBeEmpty();
        SettingsSearch.Find("zzzz-nothing", Descriptors, true, true).ShouldBeEmpty();
    }

    [Fact]
    public void TargetOf_EveryTenantDescriptorHasAPage()
    {
        // A tenant-scoped setting with no page would be unfindable — fail when a new category is added unmapped.
        // (Key pools have no editor yet and are deliberately unlisted.)
        Descriptors.Where(d => d.Scope != SettingScope.Platform && !d.Key.EndsWith("ApiKeys", System.StringComparison.Ordinal))
            .ShouldAllBe(d => SettingsSearch.TargetOf(d) != null);
        SettingsSearch.Find("anthropic api key", Descriptors, true, false, max: 10)
            .ShouldContain(h => h.Title == "Anthropic API key" && h.Target == "keys");
        SettingsSearch.Find("key pool", Descriptors, true, false).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("keys", "keys")]
    [InlineData("llm/keys", "keys")]            // pre-flattening links keep working
    [InlineData("llm", "keys")]
    [InlineData("llm/providers", "routing")]
    [InlineData("llm/data", "diagnostics")]
    [InlineData("system/appearance", "appearance")]
    [InlineData("system", "general")]
    [InlineData("audit", "audit")]
    public void Normalize_MapsLegacyTargetsToFlatPages(string target, string page)
        => SettingsLinks.Normalize(target).ShouldBe(page);
}
