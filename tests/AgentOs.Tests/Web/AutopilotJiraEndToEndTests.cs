// E1.8 — Autopilot picks a Jira ticket, end to end with fakes. Everything between the board row and the HTTP wire is
// real: the board is connected by the real WorkspaceConnector (Jira validated, token stored encrypted), the binding is
// read back by WorkspaceBoardTrackers, tickets flow through the real SourceProviderResolver → TrackerRoutingSourceProvider
// → JiraSourceProvider → JiraRestClient, get their repository from the board (TrackerRepoMap), are planned with the
// orchestrator's own mapping (ProjectOrchestrator.PlanTicketOf) and AutopilotPlanner, and the started ticket is moved,
// linked and asked on — in Jira, never on GitHub. Only Jira's HTTP responses (API-shaped fixtures), the workspace
// repository and the GitHub code host are fakes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Integration;
using AgentOs.Modules.Integration.Sources;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Workspaces;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.Modules.Workspaces.Security;
using AgentOs.Tests.Integration;
using AgentOs.Web.Services.BoardTasks;
using AgentOs.Web.Services.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class AutopilotJiraEndToEndTests
{
    private const string Tenant = "tenant-e2e";

    private readonly RecordingHandler _jiraHttp = new RecordingHandler()
        .On(HttpMethod.Get, "/rest/api/3/project/SHOP/role/10002", TrackerFixtures.JiraAdminRole)
        .On(HttpMethod.Get, "/rest/api/3/project/SHOP/role/10001", TrackerFixtures.JiraDeveloperRole)
        .On(HttpMethod.Get, "/rest/api/3/project/SHOP/role", TrackerFixtures.JiraRoles)
        .On(HttpMethod.Get, "/rest/api/3/project/SHOP", TrackerFixtures.JiraProject)
        .On(HttpMethod.Get, "/rest/api/3/group/member", TrackerFixtures.JiraGroupMembers)
        .On(HttpMethod.Post, "/rest/api/3/search/jql", TrackerFixtures.JiraSearchPage2, whenBodyContains: "nextPageToken")
        .On(HttpMethod.Post, "/rest/api/3/search/jql", TrackerFixtures.JiraSearchPage1)
        .On(HttpMethod.Get, "/rest/api/3/issue/SHOP-12/comment", TrackerFixtures.JiraComments)
        .On(HttpMethod.Post, "/rest/api/3/issue/SHOP-12/comment", """{"id":"10500"}""", HttpStatusCode.Created)
        .On(null, "/rest/api/3/issue/SHOP-12/transitions", TrackerFixtures.JiraTransitions)
        .On(HttpMethod.Post, "/rest/api/3/issue/SHOP-12/remotelink", """{"id":1}""", HttpStatusCode.Created)
        .On(HttpMethod.Get, "/rest/api/3/issue/SHOP-12", TrackerFixtures.JiraIssue)
        .On(HttpMethod.Get, "/rest/api/3/issue/SHOP-13", """{"id":"10013","key":"SHOP-13","fields":{"description":null}}""");

    private readonly IWorkspaceRepository _workspaces = Substitute.For<IWorkspaceRepository>();
    private readonly InMemoryAppConfigStore _config = new();
    private readonly ISourceProvider _github = Substitute.For<ISourceProvider>();
    private readonly IPrCreationService _prs = Substitute.For<IPrCreationService>();
    private WorkspaceEntity? _board;

    private ServiceProvider Services()
    {
        _github.Kind.Returns(SourceProviderKind.GitHub);
        _workspaces.When(r => r.AddForTenantAsync(Arg.Any<WorkspaceEntity>(), Arg.Any<CancellationToken>())).Do(ci => _board = ci.Arg<WorkspaceEntity>());
        _workspaces.GetForTenantAsync(Tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => _board);
        IReadOnlyList<WorkspaceRepoEntity> repos = [new WorkspaceRepoEntity { Owner = "octo", Repo = "shop-web", DefaultBranch = "main", TenantId = Tenant }];
        _workspaces.ListReposForTenantAsync(Tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(repos);
        var jira = new JiraSourceProvider(new HandlerHttpClientFactory(_jiraHttp));
        return new ServiceCollection()
            .AddSingleton(_workspaces)
            .AddSingleton<IAppConfigStore>(_config)
            .AddSingleton(_prs)
            .AddScoped<IBoardTrackers, WorkspaceBoardTrackers>()
            .AddScoped<IBoardRepos, WorkspaceBoardRepos>()
            .AddSingleton<ISourceProviderResolver>(sp => new SourceProviderResolver([_github, jira], sp.GetRequiredService<IServiceScopeFactory>()))
            .AddSingleton<IBoardTicketService>(sp => new BoardTicketService(sp.GetRequiredService<ISourceProviderResolver>()))
            .BuildServiceProvider();
    }

    [Fact]
    public async Task Autopilot_PicksTheApprovedJiraTicket_StartsIt_AndWritesBackToJira()
    {
        using var sp = Services();

        // 1. Connect "Shop": code on GitHub, tickets from Jira project SHOP (the Board › Projects flow).
        var connector = new WorkspaceConnector(_workspaces, sp.GetRequiredService<ISourceProviderResolver>(), _config, TimeProvider.System,
            new WorkspaceHostPolicy(Options.Create(new WorkspaceHostOptions())));
        var connected = await connector.ConnectAsync(Tenant, "member-1", new WorkspaceConnectInput("Shop", SourceProviderKind.GitHub, "octo", "org", null, null, null,
            "ghp_code", new TrackerConnectInput(TrackerFixtures.Jira with { StartState = null, DoneState = null }, "jira-token")));
        connected.Ok.ShouldBeTrue(connected.Error);
        var board = _board.ShouldNotBeNull();
        board.Autopilot = "Build";

        // 2. A pass reads the board exactly as ProjectOrchestrator.StepAsync does (the code token; the routing swaps in Jira's).
        var pat = (await _config.GetForTenantAsync(Tenant, board.CredentialRef)).ShouldNotBeNull();
        var boardDesc = new BoardDescriptor(board.Id, Tenant, board.Kind, board.ProjectOwner, board.ProjectScope, board.ProjectNumber, board.ProjectNodeId, pat, board.Project);
        var tickets = (await sp.GetRequiredService<IBoardTicketService>().ListTicketsAsync(boardDesc)).Items.Where(ProjectOrchestrator.IsWorkable).ToList();

        tickets.Select(t => t.Key).ShouldBe(["SHOP-12", "SHOP-11", "SHOP-13"]);
        tickets.ShouldAllBe(t => t.RepoOwner == "octo" && t.RepoName == "shop-web");   // the board's repository

        var provider = sp.GetRequiredService<ISourceProviderResolver>().Resolve(board.Kind);
        var plan = new List<PlanTicket>();
        foreach (var t in tickets)
        {
            var deps = ProjectOrchestrator.NeedsDependencies(t, live: false)
                ? TicketDependencies.Parse(await provider.ReadTicketBodyAsync(BoardTaskService.RepoDescriptor(board, Tenant, t.RepoOwner!, t.RepoName!, "main", pat), t.Number!.Value))
                : [];
            plan.Add(ProjectOrchestrator.PlanTicketOf(t, live: false, deps));
        }
        plan.Single(p => p.Number == 12).DependsOn.ShouldBe([11]);   // "Depends on #11" in the Jira description; SHOP-11 is done

        // 3. The rules pick SHOP-12 first (approved, p1, its dependency done).
        var actions = AutopilotPlanner.Decide(AutopilotMode.Build, plan, []);
        actions.OfType<AutopilotAction.StartTicket>().First().Number.ShouldBe(12);

        // 4. Starting it: the agents read the Jira body + the maintainer decisions…
        var repo = BoardTaskService.RepoDescriptor(board, Tenant, "octo", "shop-web", "main", pat);
        var ticket = await provider.ReadTicketAsync(repo, 12, TicketDiscussion.DefaultMaxComments);
        ticket.Body.ShouldNotBeNull().ShouldContain("Customers can enter a coupon code.");
        TicketDiscussion.ForAgents(ticket.Comments).Where(c => c.Role == TicketCommentRole.Maintainer).Select(c => c.Body)
            .ShouldBe(["Max one coupon per order.", "Codes are case-insensitive."]);

        // …the ticket moves to "In Progress" (the default start transition), the pull request names SHOP-12 and links back…
        (await TicketWrites.MoveAsync(sp, board, repo, 12, TicketMilestone.Started)).ShouldBeTrue();
        var tracker = TicketWrites.Tracker(board).ShouldNotBeNull();
        var title = PullRequestText.Title(12, "#12 Add a coupon field to checkout", "type:story", tracker);
        title.ShouldBe("feat: Add a coupon field to checkout (SHOP-12)");
        PullRequestText.Body(12, "Adds the coupon field.", null, tracker).ShouldNotContain("Closes #12");   // no GitHub issue #12 is closed by the merge
        (await TicketWrites.LinkAsync(sp, board, repo, 12, "https://github.com/octo/shop-web/pull/7", title)).ShouldBeTrue();

        // …a question for a person goes on the Jira issue, and the merge moves it to Done.
        (await TicketWrites.CommentAsync(sp, board, repo, 12, "### ❓ AgentOS.NET needs you\n\nOne coupon per customer, or per order?")).ShouldBeTrue();
        (await TicketWrites.MoveAsync(sp, board, repo, 12, TicketMilestone.Merged)).ShouldBeTrue();

        var transitions = _jiraHttp.To(HttpMethod.Post, "/issue/SHOP-12/transitions")
            .Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("transition").GetProperty("id").GetString()).ToList();
        transitions.ShouldBe(["21", "31"]);   // Start work → In Progress, then Done
        _jiraHttp.To(HttpMethod.Post, "/issue/SHOP-12/remotelink").ShouldHaveSingleItem();
        _jiraHttp.To(HttpMethod.Post, "/issue/SHOP-12/comment").ShouldHaveSingleItem();

        // Nothing went to GitHub's issues, and every Jira call used the Jira account — never the GitHub token.
        await _prs.DidNotReceiveWithAnyArgs().CommentAsync(default!, default, default!, default);
        await _prs.DidNotReceiveWithAnyArgs().CloseAsync(default!, default, default!, default);
        await _github.DidNotReceiveWithAnyArgs().ReadBoardTicketsAsync(default!, default);
        _jiraHttp.Requests.ShouldAllBe(r => r.Authorization != null && !r.Authorization.Contains("ghp_code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGitHubProjectsBoard_KeepsItsGitHubWrites()
    {
        using var sp = Services();
        var board = new WorkspaceEntity { Id = Guid.NewGuid(), TenantId = Tenant, Kind = SourceProviderKind.GitHub, ProjectNumber = 3, CredentialRef = "c" };
        var repo = BoardTaskService.RepoDescriptor(board, Tenant, "octo", "shop-web", "main", "ghp_code");
        _prs.CommentAsync(repo, 5, "hi", Arg.Any<CancellationToken>()).Returns(true);

        (await TicketWrites.CommentAsync(sp, board, repo, 5, "hi")).ShouldBeTrue();
        (await TicketWrites.MoveAsync(sp, board, repo, 5, TicketMilestone.Started)).ShouldBeFalse();   // "Closes #5" does it on GitHub
        (await TicketWrites.LinkAsync(sp, board, repo, 5, "https://github.com/octo/shop-web/pull/9", "t")).ShouldBeFalse();

        await _prs.Received(1).CommentAsync(repo, 5, "hi", Arg.Any<CancellationToken>());
        _jiraHttp.Requests.ShouldBeEmpty();
    }
}
