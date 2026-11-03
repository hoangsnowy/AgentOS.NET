// E1.8 — Azure DevOps Boards as a tracker: WIQL building (area / iteration / state / tags, quoting), work item and
// comment mapping on API-shaped fixtures, the PAT auth header, the maintainer-role mapping (Project Administrators →
// PROJECT_ADMIN, Contributors → PROJECT_MEMBER, through the Identities API), JSON-Patch state moves and PR links.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration.Sources;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Integration;

public sealed class AzureDevOpsBoardsTests
{
    private static readonly TrackerBinding Binding = TrackerFixtures.Ado;

    private static RecordingHandler Ado() => new RecordingHandler()
        .On(HttpMethod.Get, "Project Administrators", TrackerFixtures.AdoAdminsGroup)
        .On(HttpMethod.Get, "Contributors", TrackerFixtures.AdoContributorsGroup)
        .On(HttpMethod.Get, "_apis/identities?identityIds=", TrackerFixtures.AdoMemberships)
        .On(HttpMethod.Post, "/_apis/wit/wiql", TrackerFixtures.AdoWiql)
        .On(HttpMethod.Get, "/_apis/wit/workitems?ids=", TrackerFixtures.AdoWorkItems)
        .On(HttpMethod.Get, "/_apis/wit/workItems/42/comments", TrackerFixtures.AdoComments)
        .On(HttpMethod.Post, "/_apis/wit/workItems/42/comments", """{"id":4}""")
        .On(HttpMethod.Get, "/_apis/wit/workitems/42?$expand=relations", TrackerFixtures.AdoRelations)
        .On(HttpMethod.Get, "/_apis/wit/workitems/42?fields=", TrackerFixtures.AdoWorkItem)
        .On(HttpMethod.Patch, "/_apis/wit/workitems/42", """{"id":42,"rev":8}""")
        .On(HttpMethod.Post, "/_apis/wit/workitems/$Task", """{"id":77,"rev":1}""")
        .On(HttpMethod.Get, "/_apis/projects/Shop", TrackerFixtures.AdoProject);

    private static AzureDevOpsSourceProvider Provider(RecordingHandler handler) => new(new HandlerHttpClientFactory(handler));

    private static WorkspaceDescriptor Ticket(TrackerBinding? binding = null) =>
        new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), SourceProviderKind.AzureDevOps, Binding.Site, Binding.Project, Binding.Project, "main", "ado-pat")
        {
            Tracker = binding ?? Binding,
        };

    private static BoardDescriptor Board() =>
        new(Guid.NewGuid(), "t1", SourceProviderKind.AzureDevOps, Binding.Site, "org", null, null, "ado-pat", Binding.Project) { Tracker = Binding };

    [Fact]
    public void BuildWiql_NarrowsByAreaStatesAndTags_AndQuotesValues()
    {
        var wiql = AzureDevOpsBoardsClient.BuildWiql(Binding with
        {
            Filter = new TrackerFilter(AreaPath: "Shop\\O'Brien", IterationPath: "Shop\\Sprint 12", States: ["New", " Active "], Tags: ["ai:ready", "p0"]),
        });

        wiql.ShouldBe("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.State] <> 'Removed'"
            + " AND [System.AreaPath] UNDER 'Shop\\O''Brien'"
            + " AND [System.IterationPath] UNDER 'Shop\\Sprint 12'"
            + " AND [System.State] IN ('New', 'Active')"
            + " AND [System.Tags] CONTAINS 'ai:ready' AND [System.Tags] CONTAINS 'p0'"
            + " ORDER BY [System.ChangedDate] DESC");
    }

    [Theory]
    [InlineData("@CurrentIteration", " AND [System.IterationPath] = @CurrentIteration ")]
    [InlineData("@CurrentIteration('[Shop]\\Shop Team')", " AND [System.IterationPath] = @CurrentIteration('[Shop]\\Shop Team') ")]
    [InlineData("@CurrentIteration; DROP", " AND [System.IterationPath] UNDER '@CurrentIteration; DROP' ")]   // not the macro: a quoted path
    public void BuildWiql_CurrentIterationMacro_OnlyInItsExactForm(string iteration, string expected) =>
        AzureDevOpsBoardsClient.BuildWiql(Binding with { Filter = new TrackerFilter(IterationPath: iteration) }).ShouldContain(expected);

    [Fact]
    public void ParseWorkItems_MapsFieldsToTickets()
    {
        var tickets = AzureDevOpsBoardsClient.ParseWorkItems(TrackerFixtures.AdoWorkItems, Binding);

        var story = tickets.Single(t => t.Number == 42);
        story.Key.ShouldBe("AB#42");
        story.Title.ShouldBe("Coupon field");
        story.State.ShouldBe("OPEN");
        story.Status.ShouldBe("New");
        story.Labels.ShouldBe(["ai:ready", "repo:shop-web", "p0"]);
        story.AssigneeLogin.ShouldBe("mia@contoso.com");
        story.HtmlUrl.ShouldBe("https://dev.azure.com/contoso/Shop/_workitems/edit/42");
        tickets.Single(t => t.Number == 41).State.ShouldBe("CLOSED");
    }

    [Fact]
    public async Task ReadBoardTickets_RunsWiqlThenTheBatchRead_InQueryOrder_WithThePat()
    {
        var handler = Ado();
        var tickets = await Provider(handler).ReadBoardTicketsAsync(Board());

        tickets.Items.Select(t => t.Number).ShouldBe([42, 41]);    // WIQL's order (newest change first)
        var wiql = handler.To(HttpMethod.Post, "/_apis/wit/wiql").Single();
        wiql.Url.AbsolutePath.ShouldBe("/contoso/Shop/_apis/wit/wiql");
        JsonDocument.Parse(wiql.Body!).RootElement.GetProperty("query").GetString().ShouldNotBeNull().ShouldContain("[System.AreaPath] UNDER 'Shop\\Checkout'");
        handler.To(HttpMethod.Get, "/_apis/wit/workitems?ids=").Single().Url.Query.ShouldContain("ids=42,41");
        handler.Requests.ShouldAllBe(r => r.Authorization == "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":ado-pat")));
    }

    [Fact]
    public async Task ReadBoardTickets_RefusedQuery_ThrowsWithAzureDevOpsReason()
    {
        var handler = new RecordingHandler().On(HttpMethod.Post, "/_apis/wit/wiql",
            """{"$id":"1","message":"TF51011: The specified area path does not exist.","typeKey":"QueryException"}""", HttpStatusCode.BadRequest);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Provider(handler).ReadBoardTicketsAsync(Board()));
        ex.Message.ShouldContain("TF51011");
    }

    [Fact]
    public async Task ReadTicket_DescriptionAndCriteriaAsText_CommentsMappedToProjectRoles()
    {
        var handler = Ado();
        var content = await Provider(handler).ReadTicketAsync(Ticket(), 42, 10);

        content.Body.ShouldBe("Customers can enter a coupon code.\nDepends on #41\n\n- One per order\n\nAcceptance criteria:\nInvalid codes show an error");
        TicketDependencies.Parse(content.Body).ShouldBe([41]);
        content.CommentsNote.ShouldBeNull();

        content.Comments.Select(c => c.Body).ShouldBe(["Max one coupon per order.", "Codes are case-insensitive.", "Please also add gift cards!"]);   // oldest first
        content.Comments[0].AuthorAssociation.ShouldBe(TicketDiscussion.ProjectAdminAssociation);
        content.Comments[0].Author.ShouldBe("ada");
        content.Comments[1].AuthorAssociation.ShouldBe(TicketDiscussion.ProjectMemberAssociation);   // HTML flattened, object descriptor matched
        content.Comments[2].AuthorAssociation.ShouldBe("NONE");
        content.Comments.Select(c => c.Role).ShouldBe([TicketCommentRole.Maintainer, TicketCommentRole.Maintainer, TicketCommentRole.Discussion]);

        // Identities live on vssps in the cloud; the group search names the project's groups.
        handler.Requests.Where(r => r.Url.AbsolutePath.EndsWith("/_apis/identities", StringComparison.Ordinal))
            .ShouldAllBe(r => r.Url.Host == "vssps.dev.azure.com");
        handler.Requests.ShouldContain(r => Uri.UnescapeDataString(r.Url.Query).Contains("filterValue=[Shop]\\Project Administrators", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadTicket_IdentitiesUnreadable_EveryCommentIsDiscussion_AndTheFeedSaysSo()
    {
        var handler = new RecordingHandler()
            .On(HttpMethod.Get, "/_apis/identities", "", HttpStatusCode.Unauthorized)
            .On(HttpMethod.Get, "/_apis/wit/workItems/42/comments", TrackerFixtures.AdoComments)
            .On(HttpMethod.Get, "/_apis/wit/workitems/42", TrackerFixtures.AdoWorkItem);

        var content = await Provider(handler).ReadTicketAsync(Ticket(), 42, 10);

        content.CommentsNote.ShouldBe(AzureDevOpsBoardsClient.RolesUnreadableNote);
        content.Comments.ShouldAllBe(c => c.Role == TicketCommentRole.Discussion);
    }

    [Fact]
    public void RoleOf_AdminWinsOverContributor()
    {
        AzureDevOpsBoardsClient.RoleOf(["a", "c"], "A", "C").ShouldBe(TicketDiscussion.ProjectAdminAssociation);
        AzureDevOpsBoardsClient.RoleOf(["c"], "A", "C").ShouldBe(TicketDiscussion.ProjectMemberAssociation);
        AzureDevOpsBoardsClient.RoleOf(["x"], "A", "C").ShouldBeNull();
        AzureDevOpsBoardsClient.RoleOf(["a"], null, null).ShouldBeNull();
    }

    [Fact]
    public void ParseComments_AgentOsSignedHtml_KeepsItsSignature()
    {
        var json = """{"comments":[{"text":"<div>Done.</div>\n<sub>Run by the AgentOS.NET Reviewer agent</sub>","createdBy":{"uniqueName":"bot@contoso.com","id":"x"},"createdDate":"2026-11-01T00:00:00Z"}]}""";
        var c = AzureDevOpsBoardsClient.ParseComments(json).Single().Comment;
        c.Role.ShouldBe(TicketCommentRole.Excluded);   // ours, even though it was posted with the board's own token
    }

    [Fact]
    public void ParseComments_BuildService_IsABot() =>
        AzureDevOpsBoardsClient.ParseComments("""{"comments":[{"text":"x","createdBy":{"displayName":"Shop Build Service (contoso)","uniqueName":"Build\\8f1c","id":"b"}}]}""")
            .Single().Comment.AuthorIsBot.ShouldBeTrue();

    [Fact]
    public async Task MoveTicket_PatchesSystemState_AsJsonPatch()
    {
        var handler = Ado();
        (await Provider(handler).MoveTicketAsync(Ticket(), 42, TicketMilestone.Started)).ShouldBeTrue();

        var patch = handler.To(HttpMethod.Patch, "/_apis/wit/workitems/42").Single();
        patch.ContentType.ShouldBe("application/json-patch+json");
        patch.Body.ShouldBe("""[{"op":"add","path":"/fields/System.State","value":"Active"}]""");
    }

    [Fact]
    public async Task MoveTicket_NoStateConfigured_DoesNothing()
    {
        var handler = Ado();
        (await Provider(handler).MoveTicketAsync(Ticket(Binding with { DoneState = "" }), 42, TicketMilestone.Merged)).ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task LinkPullRequest_AddsAHyperlink_OnceOnly()
    {
        var handler = Ado();
        var provider = Provider(handler);

        (await provider.LinkPullRequestAsync(Ticket(), 42, "https://github.com/octo/shop-web/pull/7", "t")).ShouldBeTrue();   // already linked
        handler.To(HttpMethod.Patch, "/_apis/wit/workitems/42").ShouldBeEmpty();

        (await provider.LinkPullRequestAsync(Ticket(), 42, "https://github.com/octo/shop-web/pull/8", "feat: x (AB#42)")).ShouldBeTrue();
        var patch = handler.To(HttpMethod.Patch, "/_apis/wit/workitems/42").Single();
        using var doc = JsonDocument.Parse(patch.Body!);
        var op = doc.RootElement[0];
        op.GetProperty("path").GetString().ShouldBe("/relations/-");
        op.GetProperty("value").GetProperty("rel").GetString().ShouldBe("Hyperlink");
        op.GetProperty("value").GetProperty("url").GetString().ShouldBe("https://github.com/octo/shop-web/pull/8");
    }

    [Fact]
    public async Task CommentOnTicket_PostsMarkdown()
    {
        var handler = Ado();
        (await Provider(handler).CommentOnTicketAsync(Ticket(), 42, "**Which** currency?")).ShouldBeTrue();
        var post = handler.To(HttpMethod.Post, "/comments").Single();
        post.Url.Query.ShouldContain("format=markdown");
        post.Body.ShouldBe("""{"text":"**Which** currency?"}""");
    }

    [Fact]
    public async Task CreateTickets_CreatesTasksInTheBoardsAreaWithTags()
    {
        var handler = Ado();
        var created = await Provider(handler).CreateTicketsAsync(Board(), Ticket(), [new TicketDraft("Coupon API", "Line 1\n<b>x</b>", ["type:feature"], AiReady: true)]);

        created.ShouldHaveSingleItem().Number.ShouldBe(77);
        using var doc = JsonDocument.Parse(handler.To(HttpMethod.Post, "/$Task").Single().Body!);
        var ops = doc.RootElement.EnumerateArray().ToDictionary(o => o.GetProperty("path").GetString()!, o => o.GetProperty("value").GetString());
        ops["/fields/System.Title"].ShouldBe("Coupon API");
        ops["/fields/System.Description"].ShouldBe("<div>Line 1<br/>&lt;b&gt;x&lt;/b&gt;</div>");   // escaped: no HTML injection
        ops["/fields/System.Tags"].ShouldBe("type:feature; ai:ready");
        ops["/fields/System.AreaPath"].ShouldBe("Shop\\Checkout");
    }

    [Fact]
    public async Task Validate_ChecksTheProject_AndTheQuery()
    {
        var v = await Provider(Ado()).ValidateBoardAsync(Board());
        v.Ok.ShouldBeTrue(v.Error);
        v.Title.ShouldBe("Shop");

        var bad = await Provider(new RecordingHandler().On(HttpMethod.Get, "/_apis/projects/Shop", "<html>sign in</html>", HttpStatusCode.NonAuthoritativeInformation))
            .ValidateBoardAsync(Board());
        bad.Ok.ShouldBeFalse();
        bad.Error.ShouldNotBeNull().ShouldContain("Work Items");
    }

    [Fact]
    public async Task AzureDevOpsServer_UsesItsOwnHost_ForIdentities()
    {
        var handler = Ado();
        var onPrem = Binding with { Host = "tfs.contoso.local" };
        await Provider(handler).ReadTicketAsync(Ticket(onPrem), 42, 5);
        handler.Requests.ShouldAllBe(r => r.Url.Host == "tfs.contoso.local");
    }

    [Fact]
    public void HtmlToText_DecodesEntities_AndDropsTags() =>
        TrackerText.HtmlToText("<p>a &amp; b</p><script>x</script><br/>c").ShouldBe("a & b\nx\nc");
}
