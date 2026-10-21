// BoardTaskService — the one place a Board task starts (UI and orchestrator alike). The refusals are explicit
// results, not toasts, so a server-side caller can act on them.

using AgentOs.Domain.Pipeline;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Pipeline.Pipeline;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.Web.Services;
using AgentOs.Web.Services.BoardTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class BoardTaskServiceTests
{
    private const string Tenant = "t1";
    private static readonly Guid Board = Guid.NewGuid();

    private static (BoardTaskService Service, ISessionRepository Sessions, IWorkspaceRepository Boards, IAppConfigStore Config) Build()
    {
        var sessions = Substitute.For<ISessionRepository>();
        var boards = Substitute.For<IWorkspaceRepository>();
        var config = Substitute.For<IAppConfigStore>();
        var services = new ServiceCollection()
            .AddSingleton(sessions).AddSingleton(boards).AddSingleton(config)
            .BuildServiceProvider();
        var feed = Substitute.For<AgentOs.Domain.Sessions.ISessionRunFeed>();
        var service = new BoardTaskService(services.GetRequiredService<IServiceScopeFactory>(), feed, new TaskStopRegistry(),
            Options.Create(new PipelineOptions()));
        return (service, sessions, boards, config);
    }

    private static RemoteSessionEntity Session(Guid id) => new() { Id = id, TenantId = Tenant, WorkspaceId = Board, Title = "#7 t" };

    [Fact]
    public async Task StartAsync_UnknownTask_IsTaskNotFound()
    {
        var (service, sessions, _, _) = Build();
        sessions.ListForTenantAsync(Tenant, Arg.Any<CancellationToken>()).Returns([]);

        (await service.StartAsync(Tenant, Guid.NewGuid())).ShouldBe(TaskStart.TaskNotFound);
    }

    [Fact]
    public async Task StartAsync_BoardGone_IsBoardNotFound_AndNothingRuns()
    {
        var (service, sessions, _, _) = Build();
        var id = Guid.NewGuid();
        sessions.ListForTenantAsync(Tenant, Arg.Any<CancellationToken>()).Returns([Session(id)]);

        (await service.StartAsync(Tenant, id)).ShouldBe(TaskStart.BoardNotFound);
        await sessions.DidNotReceiveWithAnyArgs().UpdateRunResultAsync(default!, default, default!, default, default);
    }

    [Fact]
    public async Task StartAsync_NoRepos_IsNoCodeProject()
    {
        var (service, sessions, boards, _) = Build();
        var id = Guid.NewGuid();
        sessions.ListForTenantAsync(Tenant, Arg.Any<CancellationToken>()).Returns([Session(id)]);
        boards.GetForTenantAsync(Tenant, Board, Arg.Any<CancellationToken>()).Returns(new WorkspaceEntity { Id = Board, TenantId = Tenant, CredentialRef = "k" });
        sessions.ListReposForTenantAsync(Tenant, id, Arg.Any<CancellationToken>()).Returns([]);

        (await service.StartAsync(Tenant, id)).ShouldBe(TaskStart.NoCodeProject);
    }

    [Fact]
    public async Task StartAsync_NoGitHubCredential_IsNoGitHubAccess()
    {
        var (service, sessions, boards, _) = Build();
        var id = Guid.NewGuid();
        sessions.ListForTenantAsync(Tenant, Arg.Any<CancellationToken>()).Returns([Session(id)]);
        boards.GetForTenantAsync(Tenant, Board, Arg.Any<CancellationToken>()).Returns(new WorkspaceEntity { Id = Board, TenantId = Tenant, CredentialRef = "k" });
        sessions.ListReposForTenantAsync(Tenant, id, Arg.Any<CancellationToken>()).Returns([new SessionRepoEntity { Id = Guid.NewGuid(), SessionId = id, Owner = "o", Repo = "r" }]);
        // An unconfigured substitute returns default(ValueTask<string?>), i.e. no credential.

        (await service.StartAsync(Tenant, id)).ShouldBe(TaskStart.NoGitHubAccess);
    }
}
