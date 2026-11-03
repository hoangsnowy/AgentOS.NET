// E1.8 — the Jira Cloud tracker: REST v3 mapping on API-shaped fixtures, the Basic auth header (e-mail + API token),
// the maintainer-role mapping (project roles → PROJECT_ADMIN / PROJECT_MEMBER, groups expanded, apps are bots), JQL
// building, transitions and remote links.

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

public sealed class JiraSourceProviderTests
{
    private static readonly TrackerBinding Binding = TrackerFixtures.Jira;

    private static RecordingHandler Jira() => new RecordingHandler()
        .On(HttpMethod.Get, "/rest/api/3/project/SHOP/role/10002", TrackerFixtures.JiraAdminRole)
        .On(HttpMethod.Get, "/rest/api/3/project/SHOP/role/10001", TrackerFixtures.JiraDeveloperRole)
        .On(HttpMethod.Get, "/rest/api/3/project/SHOP/role", TrackerFixtures.JiraRoles)
        .On(HttpMethod.Get, "/rest/api/3/project/SHOP", TrackerFixtures.JiraProject)
        .On(HttpMethod.Get, "/rest/api/3/group/member?groupId=g-42", TrackerFixtures.JiraGroupMembers)
        .On(HttpMethod.Get, "/rest/api/3/issue/SHOP-12/comment", TrackerFixtures.JiraComments)
        .On(null, "/rest/api/3/issue/SHOP-12/transitions", TrackerFixtures.JiraTransitions)
        .On(HttpMethod.Post, "/rest/api/3/issue/SHOP-12/remotelink", """{"id":10000,"self":"…"}""", HttpStatusCode.Created)
        .On(HttpMethod.Post, "/rest/api/3/issue/SHOP-12/comment", """{"id":"10500"}""", HttpStatusCode.Created)
        .On(HttpMethod.Get, "/rest/api/3/issue/SHOP-12", TrackerFixtures.JiraIssue)
        .On(HttpMethod.Post, "/rest/api/3/issue", TrackerFixtures.JiraCreated, HttpStatusCode.Created)
        .On(HttpMethod.Post, "/rest/api/3/search/jql", TrackerFixtures.JiraSearchPage2);

    private static JiraSourceProvider Provider(RecordingHandler handler) => new(new HandlerHttpClientFactory(handler));

    private static WorkspaceDescriptor Ticket(string? tenant = null) =>
        new(Guid.NewGuid(), tenant ?? Guid.NewGuid().ToString("N"), SourceProviderKind.Jira, Binding.Site, Binding.Project, Binding.Project, "main", "jira-token")
        {
            Tracker = Binding,
        };

    private static BoardDescriptor Board() =>
        new(Guid.NewGuid(), "t1", SourceProviderKind.Jira, Binding.Site, "org", null, null, "jira-token", Binding.Project) { Tracker = Binding };

    [Fact]
    public void Basic_IsEmailColonToken_Base64()
    {
        var header = JiraRestClient.Basic(" bot@contoso.com ", "tok");
        header.Scheme.ShouldBe("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!)).ShouldBe("bot@contoso.com:tok");
    }

    [Fact]
    public async Task EveryCall_SendsTheAccountsBasicAuth()
    {
        var handler = Jira();
        await Provider(handler).ReadTicketAsync(Ticket(), 12, 5);

        handler.Requests.ShouldNotBeEmpty();
        handler.Requests.ShouldAllBe(r => r.Authorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("bot@contoso.com:jira-token")));
        handler.Requests.ShouldAllBe(r => r.Url.Host == "contoso.atlassian.net" && r.Url.Scheme == "https");
    }

    [Fact]
    public void BuildJql_ScopesToTheProject_AndAndsTheBoardsJql_WithoutItsOrderBy()
    {
        JiraRestClient.BuildJql(Binding with { Filter = new TrackerFilter(Jql: "labels = \"ai:ready\" ORDER BY rank") })
            .ShouldBe("project = \"SHOP\" AND (labels = \"ai:ready\") ORDER BY created DESC");
        JiraRestClient.BuildJql(Binding with { Filter = TrackerFilter.None }).ShouldBe("project = \"SHOP\" ORDER BY created DESC");
    }

    [Fact]
    public void ParseSearch_MapsIssues_ToTickets_AndSkipsOtherProjects()
    {
        var (tickets, next) = JiraRestClient.ParseSearch(TrackerFixtures.JiraSearchPage1, Binding);

        next.ShouldBe("CAEaAggD");
        tickets.Count.ShouldBe(2);                           // OPS-3 belongs to another project
        var open = tickets[0];
        open.Number.ShouldBe(12);
        open.Key.ShouldBe("SHOP-12");
        open.Reference.ShouldBe("SHOP-12");
        open.ItemNodeId.ShouldBe("10012");
        open.Title.ShouldBe("Add a coupon field to checkout");
        open.State.ShouldBe("OPEN");
        open.Status.ShouldBe("To Do");
        open.Kind.ShouldBe(BoardTicketKind.Issue);
        open.HtmlUrl.ShouldBe("https://contoso.atlassian.net/browse/SHOP-12");
        open.Labels.ShouldBe(["ai:ready", "repo:shop-web", "p1", "type:story"]);
        open.AssigneeLogin.ShouldBe("Mia Krystof");
        open.RepoOwner.ShouldBeNull();                       // the tracker knows no repositories
        tickets[1].State.ShouldBe("CLOSED");                 // status category "done"
        tickets[1].Labels.ShouldContain("type:bug");
    }

    [Fact]
    public async Task ReadBoardTickets_FollowsNextPageToken_UntilIsLast()
    {
        var handler = new RecordingHandler();
        var calls = 0;
        var paged = new PagedHandler(() => ++calls == 1 ? TrackerFixtures.JiraSearchPage1 : TrackerFixtures.JiraSearchPage2, handler);
        var tickets = await new JiraSourceProvider(new HandlerHttpClientFactory(paged)).ReadBoardTicketsAsync(Board());

        tickets.Items.Select(t => t.Key).ShouldBe(["SHOP-12", "SHOP-11", "SHOP-13"]);
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].Body.ShouldNotBeNull().ShouldContain("\"nextPageToken\":\"CAEaAggD\"");
        using var first = JsonDocument.Parse(handler.Requests[0].Body!);
        first.RootElement.GetProperty("jql").GetString().ShouldBe("project = \"SHOP\" AND (labels = \"ai:ready\") ORDER BY created DESC");
    }

    [Fact]
    public async Task ReadTicket_FlattensTheAdfBody_AndMapsCommentAuthorsToRoles()
    {
        var content = await Provider(Jira()).ReadTicketAsync(Ticket(), 12, 10);

        content.Body.ShouldNotBeNull();
        content.Body.ShouldContain("Customers can enter a coupon code.\nDepends on #11");
        content.Body.ShouldContain("- Invalid codes show an error");
        content.Body.ShouldContain("Ask @Mia Krystof");
        TicketDependencies.Parse(content.Body).ShouldBe([11]);
        content.CommentsNote.ShouldBeNull();

        content.Comments.Select(c => c.Body).ShouldBe(["Max one coupon per order.", "Codes are case-insensitive.", "Please also add gift cards!", "Status changed."]);
        var byText = content.Comments.ToDictionary(c => c.Body);
        byText["Max one coupon per order."].AuthorAssociation.ShouldBe(TicketDiscussion.ProjectAdminAssociation);
        byText["Codes are case-insensitive."].AuthorAssociation.ShouldBe(TicketDiscussion.ProjectMemberAssociation);   // via the group in Developers
        byText["Please also add gift cards!"].AuthorAssociation.ShouldBe("NONE");
        byText["Status changed."].AuthorIsBot.ShouldBeTrue();

        // The maintainer rule as the agents see it: admins and developers decide, the outsider is discussion, the app is out.
        byText["Max one coupon per order."].Role.ShouldBe(TicketCommentRole.Maintainer);
        byText["Codes are case-insensitive."].Role.ShouldBe(TicketCommentRole.Maintainer);
        byText["Please also add gift cards!"].Role.ShouldBe(TicketCommentRole.Discussion);
        byText["Status changed."].Role.ShouldBe(TicketCommentRole.Excluded);
        TicketDiscussion.PromptBlock(12, content.Comments, TicketDiscussionUse.Implement).ShouldContain("MAINTAINER (PROJECT_ADMIN)");
    }

    [Fact]
    public async Task ReadTicket_RolesUnreadable_EveryCommentIsDiscussion_AndTheFeedSaysSo()
    {
        var handler = new RecordingHandler()
            .On(HttpMethod.Get, "/rest/api/3/project/SHOP/role", """{"errorMessages":["You don't have permission"]}""", HttpStatusCode.Forbidden)
            .On(HttpMethod.Get, "/rest/api/3/issue/SHOP-12/comment", TrackerFixtures.JiraComments)
            .On(HttpMethod.Get, "/rest/api/3/issue/SHOP-12", TrackerFixtures.JiraIssue);

        var content = await Provider(handler).ReadTicketAsync(Ticket(), 12, 10);

        content.CommentsNote.ShouldBe(JiraRestClient.RolesUnreadableNote);
        content.Comments.Where(c => !c.AuthorIsBot).ShouldAllBe(c => c.Role == TicketCommentRole.Discussion);
    }

    [Fact]
    public void ParseRoleIds_ReadsTheIdFromTheUrl_NeverFollowsIt()
    {
        var roles = JiraRestClient.ParseRoleIds("""{"Administrators":"https://evil.example/role/10002","Broken":"https://x/role/abc"}""");
        roles.ShouldBe([("Administrators", 10002)]);
    }

    [Fact]
    public async Task MoveTicket_PicksTheTransitionByName_OrByTargetStatus()
    {
        var handler = Jira();
        var provider = Provider(handler);

        (await provider.MoveTicketAsync(Ticket(), 12, TicketMilestone.Started)).ShouldBeTrue();   // "In Progress" = the target of "Start work"
        (await provider.MoveTicketAsync(Ticket(), 12, TicketMilestone.Merged)).ShouldBeTrue();    // "Done" = a transition name

        var posts = handler.To(HttpMethod.Post, "/transitions").ToList();
        posts.Select(p => JsonDocument.Parse(p.Body!).RootElement.GetProperty("transition").GetProperty("id").GetString()).ShouldBe(["21", "31"]);
    }

    [Fact]
    public async Task MoveTicket_NoSuchTransition_IsFalse_AndPostsNothing()
    {
        var handler = Jira();
        var ok = await Provider(handler).MoveTicketAsync(Ticket() with { Tracker = Binding with { DoneState = "Shipped" } }, 12, TicketMilestone.Merged);

        ok.ShouldBeFalse();
        handler.To(HttpMethod.Post, "/transitions").ShouldBeEmpty();
    }

    [Fact]
    public async Task LinkPullRequest_PostsARemoteLink_KeyedByTheUrl()
    {
        var handler = Jira();
        (await Provider(handler).LinkPullRequestAsync(Ticket(), 12, "https://github.com/octo/shop-web/pull/7", "feat: Coupon (SHOP-12)")).ShouldBeTrue();

        using var body = JsonDocument.Parse(handler.To(HttpMethod.Post, "/remotelink").Single().Body!);
        body.RootElement.GetProperty("globalId").GetString().ShouldBe("https://github.com/octo/shop-web/pull/7");
        body.RootElement.GetProperty("object").GetProperty("title").GetString().ShouldBe("feat: Coupon (SHOP-12)");
    }

    [Fact]
    public async Task CommentOnTicket_PostsAdf()
    {
        var handler = Jira();
        (await Provider(handler).CommentOnTicketAsync(Ticket(), 12, "### ❓ AgentOS.NET needs you\n\nWhich currency?")).ShouldBeTrue();

        using var body = JsonDocument.Parse(handler.To(HttpMethod.Post, "/comment").Single().Body!);
        var doc = body.RootElement.GetProperty("body");
        doc.GetProperty("type").GetString().ShouldBe("doc");
        TrackerText.AdfToText(doc).ShouldBe("### ❓ AgentOS.NET needs you\n\nWhich currency?");
    }

    [Fact]
    public async Task TicketWrites_NeverThrow_WhenJiraIsDown()
    {
        var down = new ThrowingHandler();
        var provider = new JiraSourceProvider(new HandlerHttpClientFactory(down));
        (await provider.CommentOnTicketAsync(Ticket(), 12, "x")).ShouldBeFalse();
        (await provider.MoveTicketAsync(Ticket(), 12, TicketMilestone.Started)).ShouldBeFalse();
        (await provider.LinkPullRequestAsync(Ticket(), 12, "https://github.com/o/r/pull/1", "t")).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateTickets_CreatesTasks_WithLabelsWithoutSpaces()
    {
        var handler = Jira();
        var created = await Provider(handler).CreateTicketsAsync(Board(), Ticket(), [new TicketDraft("Coupon API", "Body", ["type:feature", "needs review"], AiReady: true)]);

        created.ShouldHaveSingleItem().Number.ShouldBe(20);
        created[0].HtmlUrl.ShouldBe("https://contoso.atlassian.net/browse/SHOP-20");
        using var body = JsonDocument.Parse(handler.To(HttpMethod.Post, "/rest/api/3/issue").Single().Body!);
        var fields = body.RootElement.GetProperty("fields");
        fields.GetProperty("project").GetProperty("key").GetString().ShouldBe("SHOP");
        fields.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldBe(["type:feature", "needs-review", "ai:ready"]);
    }

    [Fact]
    public async Task Validate_BadToken_SaysWhereToCreateOne()
    {
        var handler = new RecordingHandler().On(HttpMethod.Get, "/rest/api/3/project/SHOP", "", HttpStatusCode.Unauthorized);
        var v = await Provider(handler).ValidateBoardAsync(Board());
        v.Ok.ShouldBeFalse();
        v.Error.ShouldNotBeNull().ShouldContain("id.atlassian.com");
    }

    [Fact]
    public async Task Validate_RunsTheJqlToo()
    {
        var handler = new RecordingHandler()
            .On(HttpMethod.Get, "/rest/api/3/project/SHOP", TrackerFixtures.JiraProject)
            .On(HttpMethod.Post, "/rest/api/3/search/jql", """{"errorMessages":["Field 'sprnt' does not exist."]}""", HttpStatusCode.BadRequest);
        var v = await Provider(handler).ValidateBoardAsync(Board());
        v.Ok.ShouldBeFalse();
        v.Error.ShouldNotBeNull().ShouldContain("Field 'sprnt' does not exist.");
    }

    [Fact]
    public async Task RepositoryMembers_Refuse_JiraHoldsNoCode()
    {
        var provider = new JiraSourceProvider();
        (await provider.ValidateAsync(Ticket())).Ok.ShouldBeFalse();
        (await provider.ListRepositoriesAsync(new ConnectionCredentials(SourceProviderKind.Jira, "t"))).ShouldBeEmpty();
    }

    [Fact]
    public void AdfToText_IsBounded_OnAHostileTree()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++) { sb.Append("{\"type\":\"blockquote\",\"content\":["); }
        sb.Append("{\"type\":\"text\",\"text\":\"deep\"}");
        for (var i = 0; i < 200; i++) { sb.Append("]}"); }
        using var doc = JsonDocument.Parse(sb.ToString(), new JsonDocumentOptions { MaxDepth = 1000 });
        TrackerText.AdfToText(doc.RootElement).ShouldNotContain("deep");   // past the depth cap: dropped, not a stack overflow
    }

    private sealed class PagedHandler(Func<string> page, RecordingHandler recorder) : DelegatingHandler(recorder)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            await base.SendAsync(request, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page(), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
