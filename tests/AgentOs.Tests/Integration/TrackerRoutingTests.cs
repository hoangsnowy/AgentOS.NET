// E1.8 — the tracker routing: a board with a tracker binding has its ticket calls sent to the tracker (with the
// TRACKER's token and a descriptor carrying the binding), its repository / pull-request calls kept on the code host;
// a board without one is untouched. Tracker tickets get their repository from the board.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration.Sources;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Integration;

public sealed class TrackerRoutingTests
{
    private static readonly Guid TrackedBoard = Guid.NewGuid();
    private static readonly Guid PlainBoard = Guid.NewGuid();

    private readonly ISourceProvider _github = Substitute.For<ISourceProvider>();
    private readonly ISourceProvider _jira = Substitute.For<ISourceProvider>();
    private readonly IBoardTrackers _trackers = Substitute.For<IBoardTrackers>();
    private readonly IBoardRepos _repos = Substitute.For<IBoardRepos>();

    private ISourceProviderResolver Resolver()
    {
        _github.Kind.Returns(SourceProviderKind.GitHub);
        _jira.Kind.Returns(SourceProviderKind.Jira);
        _trackers.GetAsync("t1", TrackedBoard, Arg.Any<CancellationToken>()).Returns(new TrackerConnection(TrackerFixtures.Jira, "jira-token"));
        TrackerConnection? none = null;
        _trackers.GetAsync("t1", PlainBoard, Arg.Any<CancellationToken>()).Returns(none);
        _repos.RepoKeysAsync("t1", TrackedBoard, Arg.Any<CancellationToken>()).Returns(["octo/shop-api", "octo/shop-web"]);
        var services = new ServiceCollection().AddScoped(_ => _trackers).AddScoped(_ => _repos).BuildServiceProvider();
        return new SourceProviderResolver([_github, _jira], services.GetRequiredService<IServiceScopeFactory>());
    }

    private static WorkspaceDescriptor Repo(Guid board) =>
        new(board, "t1", SourceProviderKind.GitHub, "octo", "shop-web", null, "main", "ghp_code");

    private static BoardDescriptor Board(Guid board) =>
        new(board, "t1", SourceProviderKind.GitHub, "octo", "org", null, null, "ghp_code");

    [Fact]
    public async Task TicketCalls_OnATrackedBoard_GoToTheTracker_WithItsTokenAndBinding()
    {
        var github = Resolver().Resolve(SourceProviderKind.GitHub);
        _jira.ReadTicketAsync(Arg.Any<WorkspaceDescriptor>(), 12, 20, Arg.Any<CancellationToken>()).Returns(TicketContent.BodyOnly("from jira"));

        (await github.ReadTicketAsync(Repo(TrackedBoard), 12, 20)).Body.ShouldBe("from jira");
        await github.CommentOnTicketAsync(Repo(TrackedBoard), 12, "hi");
        await github.MoveTicketAsync(Repo(TrackedBoard), 12, TicketMilestone.Started);
        await github.LinkPullRequestAsync(Repo(TrackedBoard), 12, "https://github.com/octo/shop-web/pull/7", "t");

        await _jira.Received(1).ReadTicketAsync(Arg.Is<WorkspaceDescriptor>(d =>
            d.AccessToken == "jira-token" && d.Tracker == TrackerFixtures.Jira && d.Kind == SourceProviderKind.Jira), 12, 20, Arg.Any<CancellationToken>());
        await _jira.Received(1).CommentOnTicketAsync(Arg.Is<WorkspaceDescriptor>(d => d.AccessToken == "jira-token"), 12, "hi", Arg.Any<CancellationToken>());
        await _jira.Received(1).MoveTicketAsync(Arg.Any<WorkspaceDescriptor>(), 12, TicketMilestone.Started, Arg.Any<CancellationToken>());
        await _jira.Received(1).LinkPullRequestAsync(Arg.Any<WorkspaceDescriptor>(), 12, "https://github.com/octo/shop-web/pull/7", "t", Arg.Any<CancellationToken>());
        await _github.DidNotReceiveWithAnyArgs().ReadTicketAsync(default!, default, default, default);
        await _github.DidNotReceiveWithAnyArgs().CommentOnTicketAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task RepositoryCalls_StayOnTheCodeHost_EvenOnATrackedBoard()
    {
        var github = Resolver().Resolve(SourceProviderKind.GitHub);
        _github.ValidateAsync(Arg.Any<WorkspaceDescriptor>(), Arg.Any<CancellationToken>()).Returns(RepoValidation.Success("main"));

        (await github.ValidateAsync(Repo(TrackedBoard))).Ok.ShouldBeTrue();
        await github.ReadRepoContextAsync(Repo(TrackedBoard));
        await github.ReadDesignSkillsAsync(Repo(TrackedBoard));

        await _github.Received(1).ValidateAsync(Arg.Is<WorkspaceDescriptor>(d => d.AccessToken == "ghp_code"), Arg.Any<CancellationToken>());
        await _github.Received(1).ReadRepoContextAsync(Arg.Any<WorkspaceDescriptor>(), Arg.Any<CancellationToken>());
        await _jira.DidNotReceiveWithAnyArgs().ValidateAsync(default!, default);
    }

    [Fact]
    public async Task BoardTickets_FromTheTracker_GetTheBoardsRepositories()
    {
        var github = Resolver().Resolve(SourceProviderKind.GitHub);
        _jira.ReadBoardTicketsAsync(Arg.Any<BoardDescriptor>(), Arg.Any<CancellationToken>()).Returns(new BoardTickets(
        [
            new BoardTicket("10012", 12, "Coupon", "OPEN", BoardTicketKind.Issue, null, null, "u", ["repo:shop-web"], "To Do", null, "SHOP-12"),
            new BoardTicket("10013", 13, "API", "OPEN", BoardTicketKind.Issue, null, null, "u", [], "To Do", null, "SHOP-13"),
        ]));

        var tickets = (await github.ReadBoardTicketsAsync(Board(TrackedBoard))).Items;

        tickets.Select(t => t.RepoName).ShouldBe(["shop-web", "shop-api"]);
        await _jira.Received(1).ReadBoardTicketsAsync(Arg.Is<BoardDescriptor>(b =>
            b.Id == TrackedBoard && b.AccessToken == "jira-token" && b.Tracker == TrackerFixtures.Jira && b.Project == "SHOP"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APlainBoard_IsUntouched()
    {
        var github = Resolver().Resolve(SourceProviderKind.GitHub);
        _github.ReadTicketAsync(Arg.Any<WorkspaceDescriptor>(), 5, 20, Arg.Any<CancellationToken>()).Returns(TicketContent.BodyOnly("from github"));

        (await github.ReadTicketAsync(Repo(PlainBoard), 5, 20)).Body.ShouldBe("from github");
        await github.ReadBoardTicketsAsync(Board(PlainBoard));

        await _github.Received(1).ReadBoardTicketsAsync(Arg.Is<BoardDescriptor>(b => b.AccessToken == "ghp_code"), Arg.Any<CancellationToken>());
        await _jira.DidNotReceiveWithAnyArgs().ReadTicketAsync(default!, default, default, default);
    }

    [Fact]
    public async Task TheBindingLookup_IsCachedBriefly_PerTenantAndBoard()
    {
        var github = Resolver().Resolve(SourceProviderKind.GitHub);
        for (var i = 0; i < 5; i++) { await github.ReadTicketAsync(Repo(TrackedBoard), 12, 0); }
        await _trackers.Received(1).GetAsync("t1", TrackedBoard, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void WithoutAScopeFactory_TheResolverHandsOutTheRawProviders()
    {
        _github.Kind.Returns(SourceProviderKind.GitHub);
        new SourceProviderResolver([_github]).Resolve(SourceProviderKind.GitHub).ShouldBeSameAs(_github);
    }

    [Fact]
    public void Jira_IsResolvable_ButNeverWrapped()
    {
        var resolver = Resolver();
        resolver.Resolve(SourceProviderKind.Jira).ShouldBeSameAs(_jira);
        resolver.Resolve(SourceProviderKind.GitHub).ShouldBeOfType<TrackerRoutingSourceProvider>().Inner.ShouldBeSameAs(_github);
    }
}
