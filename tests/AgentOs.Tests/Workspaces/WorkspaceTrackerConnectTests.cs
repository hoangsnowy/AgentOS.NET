// E1.8 — connecting a board whose tickets come from Azure DevOps Boards or Jira: the tracker is validated with its own
// token before anything is stored, the token goes encrypted under the board's tracker key (never on the row), the row
// keeps the binding, and the code host stays GitHub / Azure Repos. IBoardTrackers reads it back.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Workspaces;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.Modules.Workspaces.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Workspaces;

public sealed class WorkspaceTrackerConnectTests
{
    private readonly IWorkspaceRepository _repo = Substitute.For<IWorkspaceRepository>();
    private readonly ISourceProvider _github = Substitute.For<ISourceProvider>();
    private readonly ISourceProvider _ado = Substitute.For<ISourceProvider>();
    private readonly ISourceProvider _jira = Substitute.For<ISourceProvider>();
    private readonly ISourceProviderResolver _resolver = Substitute.For<ISourceProviderResolver>();
    private readonly InMemoryAppConfigStore _credentials = new();

    private static readonly TrackerBinding Jira = new(SourceProviderKind.Jira, "contoso.atlassian.net/", " SHOP ", "bot@contoso.com",
        new TrackerFilter(Jql: "labels = \"ai:ready\""), null, null);

    private WorkspaceConnector Sut(params string[] allowedHosts)
    {
        foreach (var (kind, p) in new[] { (SourceProviderKind.GitHub, _github), (SourceProviderKind.AzureDevOps, _ado), (SourceProviderKind.Jira, _jira) })
        {
            p.Kind.Returns(kind);
            _resolver.TryResolve(kind, out Arg.Any<ISourceProvider?>()!).Returns(ci => { ci[1] = p; return true; });
        }
        var options = new WorkspaceHostOptions();
        foreach (var h in allowedHosts) { options.AllowedHosts.Add(h); }
        return new WorkspaceConnector(_repo, _resolver, _credentials, TimeProvider.System, new WorkspaceHostPolicy(Options.Create(options)));
    }

    private static WorkspaceConnectInput GitHubCodeWith(TrackerBinding tracker, string? trackerToken = "jira-token") =>
        new("Shop", SourceProviderKind.GitHub, "octo", "org", ProjectNumber: 9, null, null, "ghp_code", new TrackerConnectInput(tracker, trackerToken));

    [Fact]
    public async Task Connect_Jira_ValidatesWithItsToken_StoresItEncrypted_AndKeepsTheBindingOnTheRow()
    {
        var sut = Sut();
        _jira.ValidateBoardAsync(Arg.Any<BoardDescriptor>(), Arg.Any<CancellationToken>()).Returns(BoardValidation.Success("10000", "Shop"));

        var result = await sut.ConnectAsync("tenant-1", "user-1", GitHubCodeWith(Jira));

        result.Ok.ShouldBeTrue(result.Error);
        var row = result.Workspace!;
        row.Kind.ShouldBe(SourceProviderKind.GitHub);          // the code still lives on GitHub
        row.ProjectNumber.ShouldBeNull();                       // no GitHub Projects board: the tickets are Jira's
        row.HasTickets.ShouldBeTrue();
        var binding = row.ReadTracker().ShouldNotBeNull();
        binding.Site.ShouldBe("https://contoso.atlassian.net");   // normalized to the https origin
        binding.Project.ShouldBe("SHOP");
        (binding.StartState, binding.DoneState).ShouldBe(("In Progress", "Done"));   // defaults
        row.Tracker.ShouldNotBeNull().ShouldNotContain("jira-token");
        (await _credentials.GetForTenantAsync("tenant-1", TrackerBinding.TokenKey(row.Id))).ShouldBe("jira-token");
        (await _credentials.GetForTenantAsync("tenant-1", row.CredentialRef)).ShouldBe("ghp_code");
        (await _credentials.GetForTenantAsync("tenant-2", TrackerBinding.TokenKey(row.Id))).ShouldBeNull();
        await _jira.Received(1).ValidateBoardAsync(Arg.Is<BoardDescriptor>(b => b.AccessToken == "jira-token" && b.Tracker != null && b.Kind == SourceProviderKind.Jira),
            Arg.Any<CancellationToken>());
        await _github.DidNotReceiveWithAnyArgs().ValidateBoardAsync(default!, default);   // no Projects board to validate
    }

    [Fact]
    public async Task Connect_TrackerRefuses_StoresNothing()
    {
        var sut = Sut();
        _jira.ValidateBoardAsync(Arg.Any<BoardDescriptor>(), Arg.Any<CancellationToken>()).Returns(BoardValidation.Fail("Jira rejected the e-mail + API token."));

        var result = await sut.ConnectAsync("tenant-1", "user-1", GitHubCodeWith(Jira));

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("Jira rejected the e-mail + API token.");
        await _repo.DidNotReceiveWithAnyArgs().AddForTenantAsync(default!, default);
    }

    [Fact]
    public async Task Connect_InvalidBinding_IsRefusedBeforeAnyCall()
    {
        var sut = Sut();
        var result = await sut.ConnectAsync("tenant-1", "user-1", GitHubCodeWith(Jira with { Account = null }));

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("e-mail");
        await _jira.DidNotReceiveWithAnyArgs().ValidateBoardAsync(default!, default);
    }

    [Fact]
    public async Task Connect_JiraDataCenter_NeedsItsHostAllowed()
    {
        var onPrem = Jira with { Site = "https://jira.contoso.com" };
        var refused = await Sut().ConnectAsync("tenant-1", "user-1", GitHubCodeWith(onPrem));
        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("allowed-hosts");

        _jira.ValidateBoardAsync(Arg.Any<BoardDescriptor>(), Arg.Any<CancellationToken>()).Returns(BoardValidation.Success("1", "Shop"));
        (await Sut("jira.contoso.com").ConnectAsync("tenant-1", "user-1", GitHubCodeWith(onPrem))).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Connect_JiraToken_IsRequired()
    {
        var result = await Sut().ConnectAsync("tenant-1", "user-1", GitHubCodeWith(Jira, trackerToken: " "));
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("API token");
    }

    [Fact]
    public async Task Connect_AzureBoards_SameOrganizationAsTheCode_ReusesTheCodePat()
    {
        var sut = Sut();
        _ado.ValidateBoardAsync(Arg.Any<BoardDescriptor>(), Arg.Any<CancellationToken>()).Returns(BoardValidation.Success("p", "Shop"));
        var boards = new TrackerBinding(SourceProviderKind.AzureDevOps, "contoso", "Shop", null, TrackerFilter.None, null, null);
        var input = new WorkspaceConnectInput("Shop", SourceProviderKind.AzureDevOps, "contoso", "org", null, "Shop", null, "ado-pat",
            new TrackerConnectInput(boards, null));

        var result = await sut.ConnectAsync("tenant-1", "user-1", input);

        result.Ok.ShouldBeTrue(result.Error);
        (await _credentials.GetForTenantAsync("tenant-1", TrackerBinding.TokenKey(result.Workspace!.Id))).ShouldBe("ado-pat");
        result.Workspace.ReadTracker()!.StartState.ShouldBe("Active");
    }

    [Fact]
    public async Task Connect_AzureBoards_OtherOrganization_NeedsItsOwnPat()
    {
        var boards = new TrackerBinding(SourceProviderKind.AzureDevOps, "fabrikam", "Shop", null, TrackerFilter.None, null, null);
        var result = await Sut().ConnectAsync("tenant-1", "user-1", GitHubCodeWith(boards, trackerToken: null));
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("PAT");
    }

    [Fact]
    public async Task Connect_JiraAsTheCodeHost_IsRefused()
    {
        var input = new WorkspaceConnectInput("Shop", SourceProviderKind.Jira, "x", "org", null, "SHOP", null, "t");
        var result = await Sut().ConnectAsync("tenant-1", "user-1", input);
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("not code");
    }

    [Fact]
    public async Task BoardTrackers_ReadsTheBindingAndToken_TenantExplicitly()
    {
        var id = Guid.NewGuid();
        var row = new WorkspaceEntity { Id = id, TenantId = "tenant-1", Kind = SourceProviderKind.GitHub, Tracker = TrackerFixturesForWorkspaces.Jira.Serialize() };
        _repo.GetForTenantAsync("tenant-1", id, Arg.Any<CancellationToken>()).Returns(row);
        await _credentials.SetForTenantAsync("tenant-1", TrackerBinding.TokenKey(id), "jira-token");
        var trackers = new WorkspaceBoardTrackers(_repo, _credentials);

        var connection = (await trackers.GetAsync("tenant-1", id)).ShouldNotBeNull();
        connection.AccessToken.ShouldBe("jira-token");
        connection.Binding.Project.ShouldBe("SHOP");
        (await trackers.GetAsync("tenant-2", id)).ShouldBeNull();        // another tenant's board doesn't exist for it

        row.Tracker = null;
        (await trackers.GetAsync("tenant-1", id)).ShouldBeNull();        // a GitHub Projects board has no tracker
    }

    private static class TrackerFixturesForWorkspaces
    {
        public static readonly TrackerBinding Jira = new(SourceProviderKind.Jira, "https://contoso.atlassian.net", "SHOP", "bot@contoso.com",
            TrackerFilter.None, "In Progress", "Done");
    }
}
