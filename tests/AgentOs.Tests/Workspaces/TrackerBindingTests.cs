// E1.8 — the tracker binding a board stores, the ticket keys and URLs people see, which repository a tracker ticket's
// code lives in, the pull request text for a tracker ticket (never "Closes #N"), and the maintainer associations.

using System;
using AgentOs.Domain.Workspaces;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Workspaces;

public sealed class TrackerBindingTests
{
    private static readonly TrackerBinding Jira = new(SourceProviderKind.Jira, "https://contoso.atlassian.net", "SHOP", "bot@contoso.com",
        new TrackerFilter(Jql: "labels = \"ai:ready\""), "In Progress", "Done");

    private static readonly TrackerBinding Ado = new(SourceProviderKind.AzureDevOps, "contoso", "Shop", null,
        new TrackerFilter("Shop\\Checkout", "@CurrentIteration", ["New"], ["ai:ready"]), "Active", "Closed");

    [Fact]
    public void Serialize_RoundTrips_AndNeverCarriesAToken()
    {
        var json = Ado.Serialize();
        json.ShouldContain("\"kind\":\"AzureDevOps\"");
        json.ShouldNotContain("token", Case.Insensitive);
        var back = TrackerBinding.Parse(json).ShouldNotBeNull();
        back.Site.ShouldBe("contoso");
        back.Filter.AreaPath.ShouldBe("Shop\\Checkout");
        back.Filter.States.ShouldBe(["New"]);
        back.DoneState.ShouldBe("Closed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{\"kind\":\"GitHub\",\"site\":\"x\",\"project\":\"y\"}")]   // GitHub is no tracker
    public void Parse_NoBinding_IsNull(string? json) => TrackerBinding.Parse(json).ShouldBeNull();

    [Fact]
    public void Keys_AndUrls_PerTracker()
    {
        Jira.TicketKey(12).ShouldBe("SHOP-12");
        Jira.TicketUrl(12).ShouldBe("https://contoso.atlassian.net/browse/SHOP-12");
        Ado.TicketKey(42).ShouldBe("AB#42");
        Ado.TicketUrl(42).ShouldBe("https://dev.azure.com/contoso/Shop/_workitems/edit/42");
        (Ado with { Host = "tfs.contoso.local" }).TicketUrl(42).ShouldBe("https://tfs.contoso.local/contoso/Shop/_workitems/edit/42");
    }

    [Theory]
    [InlineData("SHOP-12", 12)]
    [InlineData("shop-7", 7)]
    [InlineData("OPS-12", null)]
    [InlineData("SHOP-", null)]
    [InlineData("SHOP-x", null)]
    [InlineData(null, null)]
    public void NumberOfKey_OnlyTheBoundProject(string? key, int? expected) => Jira.NumberOfKey(key).ShouldBe(expected);

    [Theory]
    [InlineData("contoso.atlassian.net", "https://contoso.atlassian.net")]
    [InlineData("https://contoso.atlassian.net/jira/software/projects/SHOP", "https://contoso.atlassian.net")]
    [InlineData("http://contoso.atlassian.net", null)]   // https only
    public void JiraBaseUrl_IsTheHttpsOrigin(string site, string? expected) => (Jira with { Site = site }).JiraBaseUrl().ShouldBe(expected);

    [Fact]
    public void Problem_ExplainsWhatIsMissing()
    {
        Jira.Problem().ShouldBeNull();
        Ado.Problem().ShouldBeNull();
        (Jira with { Account = null }).Problem().ShouldNotBeNull().ShouldContain("e-mail");
        (Jira with { Project = "SH OP" }).Problem().ShouldNotBeNull().ShouldContain("project key");
        (Jira with { Site = "http://contoso.atlassian.net" }).Problem().ShouldNotBeNull().ShouldContain("https");
        (Ado with { Site = "https://dev.azure.com/contoso" }).Problem().ShouldNotBeNull().ShouldContain("not a URL");
        (Ado with { Project = " " }).Problem().ShouldNotBeNull().ShouldContain("project");
        (Ado with { Kind = SourceProviderKind.GitHub }).Problem().ShouldNotBeNull();
    }

    [Fact]
    public void TokenKey_IsPerBoard_AndApartFromTheCodeToken()
    {
        var id = Guid.NewGuid();
        TrackerBinding.TokenKey(id).ShouldBe($"workspace/{id:N}/tracker-token");
        TrackerBinding.TokenKey(id).ShouldNotBe($"workspace/{id:N}/token");
    }

    // ── Which repository a tracker ticket's code lives in ──

    private static BoardTicket T(int n, params string[] labels) =>
        new(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n, $"Ticket {n}", "OPEN", BoardTicketKind.Issue, null, null, "u", labels, null, null, $"SHOP-{n}");

    [Fact]
    public void Assign_RepoLabel_ElseTheBoardsFirstRepo_UnknownRepoKeepsNone()
    {
        var tickets = TrackerRepoMap.Assign(
            [T(1), T(2, "repo:shop-api"), T(3, "Repo:octo/SHOP-WEB"), T(4, "repo:legacy")],
            ["octo/shop-web", "octo/shop-api"]);

        (tickets[0].RepoOwner, tickets[0].RepoName).ShouldBe(("octo", "shop-web"));   // default: the first repo
        (tickets[1].RepoOwner, tickets[1].RepoName).ShouldBe(("octo", "shop-api"));
        (tickets[2].RepoOwner, tickets[2].RepoName).ShouldBe(("octo", "shop-web"));   // owner/name, any case
        tickets[3].RepoOwner.ShouldBeNull();                                           // never built in the wrong place
    }

    [Fact]
    public void Assign_NoRepos_LeavesTicketsAsTheyAre() =>
        TrackerRepoMap.Assign([T(1)], []).ShouldHaveSingleItem().RepoOwner.ShouldBeNull();

    // ── Pull request text ──

    [Fact]
    public void PullRequest_ForATrackerTicket_NamesItsKey_AndNeverClosesAGitHubIssue()
    {
        PullRequestText.Title(12, "#12 Add a coupon field", "type:feature", Jira).ShouldBe("feat: Add a coupon field (SHOP-12)");
        var body = PullRequestText.Body(12, "Adds the field.", null, Jira);
        body.ShouldBe("Adds the field.\n\nTicket: [SHOP-12](https://contoso.atlassian.net/browse/SHOP-12)");
        body.ShouldNotContain("Closes #");
        PullRequestText.Body(42, "x", null, Ado).ShouldContain("[AB#42](https://dev.azure.com/contoso/Shop/_workitems/edit/42)");

        // Without a tracker: the GitHub text, unchanged.
        PullRequestText.Title(12, "#12 Add", "type:bug", null).ShouldBe("fix: Add (#12)");
        PullRequestText.Body(12, "x", null, null).ShouldEndWith("Closes #12");
    }

    // ── Maintainer associations ──

    [Theory]
    [InlineData("PROJECT_ADMIN", true)]
    [InlineData("project_member", true)]
    [InlineData("OWNER", true)]
    [InlineData("CONTRIBUTOR", false)]   // GitHub's "has contributed before" — never a maintainer
    [InlineData("NONE", false)]
    public void TrackerProjectRoles_AreMaintainers(string association, bool maintainer) =>
        TicketDiscussion.IsMaintainerAssociation(association).ShouldBe(maintainer);
}
