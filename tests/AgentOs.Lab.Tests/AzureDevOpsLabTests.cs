// Real Azure DevOps, real PAT, the lab organization/project. Proves what the AzureDevOpsSourceProvider
// supports today against the live API (repo validation + listing) and pins the known gaps as explicit
// skipped tests, so they show up in every lab run until they are implemented.

using System;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration.Sources;
using Shouldly;
using Xunit;

namespace AgentOs.Lab.Tests;

public sealed class AzureDevOpsLabTests
{
    private const string Tenant = "lab";
    private readonly AzureDevOpsSourceProvider _ado = new();

    private static WorkspaceDescriptor Repo() => new(
        Guid.NewGuid(), Tenant, SourceProviderKind.AzureDevOps, LabConfig.AzureDevOpsOrg!, LabConfig.AzureDevOpsRepo!,
        Project: LabConfig.AzureDevOpsProject, DefaultBranch: "main", AccessToken: LabConfig.AzureDevOpsToken!);

    private static void SkipUnlessConfigured()
    {
        if (LabConfig.AzureDevOpsSkipReason is { } why) { Assert.Skip(why); }
    }

    [Fact]
    public async Task Validate_PatReachesTheLabRepo()
    {
        SkipUnlessConfigured();
        var v = await _ado.ValidateAsync(Repo());
        v.Ok.ShouldBeTrue(v.Error);
        v.DefaultBranch.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ListRepositories_IncludesTheLabRepo()
    {
        SkipUnlessConfigured();
        var repos = await _ado.ListRepositoriesAsync(new ConnectionCredentials(SourceProviderKind.AzureDevOps, LabConfig.AzureDevOpsToken!, LabConfig.AzureDevOpsOrg));
        repos.ShouldContain(r => string.Equals(r.Name, LabConfig.AzureDevOpsRepo, StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Skip = "Known gap: Azure DevOps boards (work items) are not implemented — AzureDevOpsSourceProvider.ListBoards/ValidateBoard/ReadBoardTickets/CreateTickets throw NotSupported. Tracked in the plan (lab findings).")]
    public Task Boards_ListValidateReadAndCreateWorkItems() => Task.CompletedTask;

    [Fact(Skip = "Known gap: Azure DevOps repo context has no README/tree yet — ReadRepoContextAsync returns identity only, so the Requirement agent is not grounded on ADO repos. Tracked in the plan (lab findings).")]
    public Task ReadRepoContext_ReturnsReadme() => Task.CompletedTask;
}
