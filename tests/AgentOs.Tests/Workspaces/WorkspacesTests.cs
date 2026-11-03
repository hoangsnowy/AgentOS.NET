// M2 — Workspaces seam: descriptor validation, the by-kind provider resolver, and the Azure DevOps
// provider's honest graceful-fail (wired but deferred). Repo persistence reuses the proven
// tenant-query-filter pattern (Pipeline/Tenants/AppConfig) and is exercised by the AppHost smoke.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration.Sources;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Workspaces;

public sealed class WorkspacesTests
{
    // ---- WorkspaceDescriptor.Validate ----

    [Fact]
    public void Descriptor_Validate_AllFieldsValid_DoesNotThrow()
    {
        var d = new WorkspaceDescriptor(
            Guid.NewGuid(), "tenant-1", SourceProviderKind.GitHub,
            "octocat", "hello", null, "main", "ghp_token");
        Should.NotThrow(() => d.Validate());
    }

    [Theory]
    [InlineData("", "owner", "repo", "tok")]      // no tenant
    [InlineData("t1", "", "repo", "tok")]          // no owner
    [InlineData("t1", "owner", "", "tok")]         // no repo
    [InlineData("t1", "owner", "repo", "")]        // no token
    public void Descriptor_Validate_MissingRequired_Throws(string tenant, string owner, string repo, string token)
    {
        var d = new WorkspaceDescriptor(
            Guid.NewGuid(), tenant, SourceProviderKind.GitHub, owner, repo, null, "main", token);
        Should.Throw<ArgumentException>(() => d.Validate());
    }

    [Fact]
    public void Descriptor_Validate_AzureDevOpsWithoutProject_Throws()
    {
        var d = new WorkspaceDescriptor(
            Guid.NewGuid(), "t1", SourceProviderKind.AzureDevOps,
            "org", "repo", Project: null, "main", "pat");
        Should.Throw<ArgumentException>(() => d.Validate());
    }

    [Fact]
    public void Descriptor_Validate_AzureDevOpsWithProject_DoesNotThrow()
    {
        var d = new WorkspaceDescriptor(
            Guid.NewGuid(), "t1", SourceProviderKind.AzureDevOps,
            "org", "repo", Project: "proj", "main", "pat");
        Should.NotThrow(() => d.Validate());
    }

    // ---- RepoValidation factory ----

    [Fact]
    public void RepoValidation_Success_CarriesBranch()
    {
        var v = RepoValidation.Success("develop");
        v.Ok.ShouldBeTrue();
        v.DefaultBranch.ShouldBe("develop");
        v.Error.ShouldBeNull();
    }

    [Fact]
    public void RepoValidation_Fail_CarriesReason()
    {
        var v = RepoValidation.Fail("nope");
        v.Ok.ShouldBeFalse();
        v.Error.ShouldBe("nope");
    }

    // ---- SourceProviderResolver ----

    [Fact]
    public void Resolver_ResolvesEachRegisteredKind()
    {
        var resolver = new SourceProviderResolver(new ISourceProvider[]
        {
            new GitHubSourceProvider(),
            new AzureDevOpsSourceProvider(),
        });

        resolver.Resolve(SourceProviderKind.GitHub).ShouldBeOfType<GitHubSourceProvider>();
        resolver.Resolve(SourceProviderKind.AzureDevOps).ShouldBeOfType<AzureDevOpsSourceProvider>();
    }

    [Fact]
    public void Resolver_TryResolve_ReturnsFalseForUnregistered()
    {
        var resolver = new SourceProviderResolver(new ISourceProvider[] { new GitHubSourceProvider() });

        resolver.TryResolve(SourceProviderKind.GitHub, out var gh).ShouldBeTrue();
        gh.ShouldNotBeNull();
        resolver.TryResolve(SourceProviderKind.AzureDevOps, out var ado).ShouldBeFalse();
        ado.ShouldBeNull();
    }

    [Fact]
    public void Resolver_Resolve_ThrowsForUnregistered()
    {
        var resolver = new SourceProviderResolver(new ISourceProvider[] { new GitHubSourceProvider() });
        Should.Throw<InvalidOperationException>(() => resolver.Resolve(SourceProviderKind.AzureDevOps));
    }

    // ---- AzureDevOpsSourceProvider: repos live over REST; boards a later milestone ----
    // (Validate / ListRepositories make live REST calls — exercised by the user against a real org;
    //  the response parser is unit-tested in AzureDevOpsRestClientTests.)

    [Fact]
    public async Task AzureDevOps_ReadRepoContext_ReturnsIdentityAndBranch()
    {
        var ado = new AzureDevOpsSourceProvider();
        var d = new WorkspaceDescriptor(
            Guid.NewGuid(), "t1", SourceProviderKind.AzureDevOps, "acme", "api", "Platform", "main", "pat");

        var ctx = await ado.ReadRepoContextAsync(d, CancellationToken.None);

        ctx.FullName.ShouldBe("acme/Platform/api");
        ctx.DefaultBranch.ShouldBe("main");
    }

    [Fact]
    public async Task AzureDevOps_BoardWithoutTrackerBinding_NotSupported()
    {
        // E1.8: Azure DevOps work items are read through a board's tracker binding; a bare board descriptor has none.
        var ado = new AzureDevOpsSourceProvider();
        var creds = new ConnectionCredentials(SourceProviderKind.AzureDevOps, "pat");
        var board = new BoardDescriptor(Guid.NewGuid(), "t1", SourceProviderKind.AzureDevOps, "org", "org", null, null, "pat", "proj");

        (await ado.ListBoardsAsync(creds, CancellationToken.None)).ShouldBeEmpty();
        (await ado.ValidateBoardAsync(board, CancellationToken.None)).Ok.ShouldBeFalse();
        await Should.ThrowAsync<NotSupportedException>(() => ado.ReadBoardTicketsAsync(board, CancellationToken.None));
    }

    [Fact]
    public void Providers_ReportTheirKind()
    {
        new GitHubSourceProvider().Kind.ShouldBe(SourceProviderKind.GitHub);
        new AzureDevOpsSourceProvider().Kind.ShouldBe(SourceProviderKind.AzureDevOps);
        new JiraSourceProvider().Kind.ShouldBe(SourceProviderKind.Jira);
    }
}
