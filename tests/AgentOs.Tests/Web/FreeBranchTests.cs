// A fresh attempt picks a branch an earlier, interrupted attempt didn't leave on the remote (#15 on the lab finished its
// work and could not push: issue-15-ai-fix already existed, and the agent may not force-push).

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Web.Services.BoardTasks;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class FreeBranchTests
{
    private static readonly WorkspaceDescriptor Repo = new(Guid.NewGuid(), "t", SourceProviderKind.GitHub, "o", "r", null, "main", "pat");

    private static IPrCreationService Remote(params string[] existing)
    {
        var prs = Substitute.For<IPrCreationService>();
        prs.BranchExistsAsync(Arg.Any<WorkspaceDescriptor>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<bool?>(Array.IndexOf(existing, ci.ArgAt<string>(1)) >= 0));
        return prs;
    }

    [Fact]
    public async Task FreeBranch_NothingOnTheRemote_KeepsTheDefault() =>
        (await BoardTaskService.FreeBranchAsync(Remote(), Repo, 15)).ShouldBeNull();

    [Fact]
    public async Task FreeBranch_EarlierAttemptsLeftBranches_PicksTheNextFreeOne() =>
        (await BoardTaskService.FreeBranchAsync(Remote("issue-15-ai-fix", "issue-15-ai-fix-2"), Repo, 15)).ShouldBe("issue-15-ai-fix-3");

    [Fact]
    public async Task FreeBranch_CannotCheck_KeepsTheDefault()
    {
        var prs = Substitute.For<IPrCreationService>();
        prs.BranchExistsAsync(Arg.Any<WorkspaceDescriptor>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(null));
        (await BoardTaskService.FreeBranchAsync(prs, Repo, 15)).ShouldBeNull();
    }

    [Fact]
    public async Task FreeBranch_NoTicketNumber_KeepsTheDefault() =>
        (await BoardTaskService.FreeBranchAsync(Remote("issue-0-ai-fix"), Repo, 0)).ShouldBeNull();
}
