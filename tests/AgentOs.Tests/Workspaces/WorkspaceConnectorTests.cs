// Board reshape — unit tests for the connect-a-board + add-a-repo flow, exercised tenant-explicitly
// so it works from both the HTTP endpoint and the desktop circuit.

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
using NSubstitute.ReturnsExtensions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Workspaces;

public sealed class WorkspaceConnectorTests
{
    private readonly IWorkspaceRepository _repo = Substitute.For<IWorkspaceRepository>();
    private readonly ISourceProvider _provider = Substitute.For<ISourceProvider>();
    private readonly ISourceProviderResolver _resolver = Substitute.For<ISourceProviderResolver>();
    private readonly InMemoryAppConfigStore _credentials = new();
    private readonly IWorkspaceHostPolicy _hostPolicy =
        new WorkspaceHostPolicy(Options.Create(new WorkspaceHostOptions()));

    private WorkspaceConnector Sut()
    {
        _provider.Kind.Returns(SourceProviderKind.GitHub);
        _resolver.TryResolve(SourceProviderKind.GitHub, out Arg.Any<ISourceProvider?>()!)
            .Returns(ci => { ci[1] = _provider; return true; });
        return new WorkspaceConnector(_repo, _resolver, _credentials, TimeProvider.System, _hostPolicy);
    }

    private static WorkspaceConnectInput BoardInput(int? number = 5, string token = "ghp_x") =>
        new("My board", SourceProviderKind.GitHub, "octo-org", "org", number, null, null, token);

    [Fact]
    public async Task ConnectAsync_ValidBoard_PersistsBoard_AndStoresEncryptedToken()
    {
        var sut = Sut();
        _provider.ValidateBoardAsync(Arg.Any<BoardDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(BoardValidation.Success("PVT_node", "My board"));

        var result = await sut.ConnectAsync("tenant-1", "user-1", BoardInput());

        result.Ok.ShouldBeTrue();
        result.Workspace.ShouldNotBeNull();
        result.Workspace!.TenantId.ShouldBe("tenant-1");
        result.Workspace.ProjectOwner.ShouldBe("octo-org");
        result.Workspace.ProjectScope.ShouldBe("org");
        result.Workspace.ProjectNumber.ShouldBe(5);
        result.Workspace.ProjectNodeId.ShouldBe("PVT_node");       // cached from the validation result
        result.Workspace.CredentialRef.ShouldStartWith("workspace/"); // row keeps only the reference
        await _repo.Received(1).AddForTenantAsync(Arg.Any<WorkspaceEntity>(), Arg.Any<CancellationToken>());
        (await _credentials.GetForTenantAsync("tenant-1", result.Workspace.CredentialRef)).ShouldBe("ghp_x");
        (await _credentials.GetForTenantAsync("tenant-2", result.Workspace.CredentialRef)).ShouldBeNull();   // tenant-partitioned
    }

    [Fact]
    public async Task ConnectAsync_BoardValidationFails_ReturnsError_AndPersistsNothing()
    {
        var sut = Sut();
        _provider.ValidateBoardAsync(Arg.Any<BoardDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(BoardValidation.Fail("no such board"));

        var result = await sut.ConnectAsync("tenant-1", "user-1", BoardInput());

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("no such board");
        await _repo.DidNotReceive().AddForTenantAsync(Arg.Any<WorkspaceEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectAsync_NoBoardNumber_ConnectsReposOnly_WithoutValidatingBoard()
    {
        var sut = Sut();

        var result = await sut.ConnectAsync("tenant-1", "user-1", BoardInput(number: null));

        result.Ok.ShouldBeTrue();
        result.Workspace!.ProjectNumber.ShouldBeNull();
        result.Workspace.ProjectNodeId.ShouldBeNull();
        await _provider.DidNotReceive().ValidateBoardAsync(Arg.Any<BoardDescriptor>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddForTenantAsync(Arg.Any<WorkspaceEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectAsync_MissingRequiredFields_ReturnsError()
    {
        var sut = Sut();
        var result = await sut.ConnectAsync("tenant-1", "user-1", BoardInput(token: ""));
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ConnectAsync_NoProviderForKind_ReturnsError()
    {
        var resolver = Substitute.For<ISourceProviderResolver>();
        resolver.TryResolve(Arg.Any<SourceProviderKind>(), out Arg.Any<ISourceProvider?>()!)
            .Returns(ci => { ci[1] = null; return false; });
        var sut = new WorkspaceConnector(_repo, resolver, _credentials, TimeProvider.System, _hostPolicy);

        var result = await sut.ConnectAsync("tenant-1", "user-1", BoardInput());

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("provider");
    }

    [Fact]
    public async Task ConnectAsync_HostNotAllowlisted_ReturnsError_AndContactsNothing()
    {
        var sut = Sut();
        var input = new WorkspaceConnectInput(
            "My board", SourceProviderKind.GitHub, "octo-org", "org", 5, null, "https://evil.example.com", "ghp_x");

        var result = await sut.ConnectAsync("tenant-1", "user-1", input);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("allowed-hosts");
        await _provider.DidNotReceive().ValidateBoardAsync(Arg.Any<BoardDescriptor>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddForTenantAsync(Arg.Any<WorkspaceEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectAsync_PublicHost_IsAllowed()
    {
        var sut = Sut();
        _provider.ValidateBoardAsync(Arg.Any<BoardDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(BoardValidation.Success("PVT_node", "My board"));
        var input = new WorkspaceConnectInput(
            "My board", SourceProviderKind.GitHub, "octo-org", "org", 5, null, "github.com", "ghp_x");

        var result = await sut.ConnectAsync("tenant-1", "user-1", input);

        result.Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ListBoardsAsync_HostNotAllowlisted_ReturnsEmpty_WithoutCallingProvider()
    {
        var sut = Sut();

        var boards = await sut.ListBoardsAsync(SourceProviderKind.GitHub, "octo-org", "ghp_x", "https://evil.example.com");

        boards.ShouldBeEmpty();
        await _provider.DidNotReceive().ListBoardsAsync(Arg.Any<ConnectionCredentials>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRepoAsync_ValidRepo_PersistsRepoUnderBoard_WithResolvedBranch()
    {
        var sut = Sut();
        var boardId = Guid.NewGuid();
        _repo.GetForTenantAsync("tenant-1", boardId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEntity
            {
                Id = boardId, TenantId = "tenant-1", Kind = SourceProviderKind.GitHub, CredentialRef = "workspace/x/token",
            });
        await _credentials.SetForTenantAsync("tenant-1", "workspace/x/token", "ghp_x");
        _provider.ValidateAsync(Arg.Any<WorkspaceDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(RepoValidation.Success("develop"));

        var result = await sut.AddRepoAsync("tenant-1", boardId, "octo-org", "api", null);

        result.Ok.ShouldBeTrue();
        result.Repo.ShouldNotBeNull();
        result.Repo!.Owner.ShouldBe("octo-org");
        result.Repo.Repo.ShouldBe("api");
        result.Repo.DefaultBranch.ShouldBe("develop");          // taken from the validation result
        result.Repo.WorkspaceId.ShouldBe(boardId);
        await _repo.Received(1).AddRepoForTenantAsync(Arg.Any<WorkspaceRepoEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRepoAsync_RepoValidationFails_ReturnsError_AndPersistsNothing()
    {
        var sut = Sut();
        var boardId = Guid.NewGuid();
        _repo.GetForTenantAsync("tenant-1", boardId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEntity
            {
                Id = boardId, TenantId = "tenant-1", Kind = SourceProviderKind.GitHub, CredentialRef = "workspace/x/token",
            });
        await _credentials.SetForTenantAsync("tenant-1", "workspace/x/token", "ghp_x");
        _provider.ValidateAsync(Arg.Any<WorkspaceDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(RepoValidation.Fail("repo not found"));

        var result = await sut.AddRepoAsync("tenant-1", boardId, "octo-org", "api", null);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("repo not found");
        await _repo.DidNotReceive().AddRepoForTenantAsync(Arg.Any<WorkspaceRepoEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRepoAsync_BoardNotFound_ReturnsError()
    {
        var sut = Sut();
        var boardId = Guid.NewGuid();
        _repo.GetForTenantAsync("tenant-1", boardId, Arg.Any<CancellationToken>())
            .Returns(default(WorkspaceEntity));

        var result = await sut.AddRepoAsync("tenant-1", boardId, "octo-org", "api", null);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    // ── Find-boards picker ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListBoardsAsync_DelegatesToProvider_WithCredentials()
    {
        var sut = Sut();
        _provider.ListBoardsAsync(Arg.Any<ConnectionCredentials>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new BoardSummary(5, "PVT_a", "Roadmap", "org", "octo-org"),
                new BoardSummary(6, "PVT_b", "Bugs", "org", "octo-org"),
            });

        var boards = await sut.ListBoardsAsync(SourceProviderKind.GitHub, "octo-org", "ghp_x");

        boards.Count.ShouldBe(2);
        boards[0].Number.ShouldBe(5);
        boards[0].Title.ShouldBe("Roadmap");
        await _provider.Received(1).ListBoardsAsync(
            Arg.Is<ConnectionCredentials>(c => c!.Owner == "octo-org" && c.AccessToken == "ghp_x"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListBoardsAsync_MissingOwnerOrToken_ReturnsEmpty_WithoutCallingProvider()
    {
        var sut = Sut();

        (await sut.ListBoardsAsync(SourceProviderKind.GitHub, "", "ghp_x")).ShouldBeEmpty();
        (await sut.ListBoardsAsync(SourceProviderKind.GitHub, "octo-org", "")).ShouldBeEmpty();

        await _provider.DidNotReceive().ListBoardsAsync(Arg.Any<ConnectionCredentials>(), Arg.Any<CancellationToken>());
    }
}
