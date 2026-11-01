// POST /settings/Github/test (GAP-08 follow-up): the desktop can't read a saved GitHub PAT any more, so split mode used
// to answer "Enter a token first." for a perfectly good saved token. The endpoint probes GitHub with the typed token,
// else the STORED one server-side, and returns a plain-language result that never contains the token.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Endpoints;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.AppConfig;

public sealed class SettingsGitHubTestEndpointTests
{
    private const string SavedPat = "ghp_SAVEDSAVEDSAVED1234";
    private const string TypedPat = "ghp_TYPEDTYPEDTYPED9876";

    private sealed class Resolver(ISourceProvider? provider) : ISourceProviderResolver
    {
        public ISourceProvider Resolve(SourceProviderKind kind) => provider ?? throw new InvalidOperationException("none");

        public bool TryResolve(SourceProviderKind kind, out ISourceProvider? resolved)
        {
            resolved = provider;
            return provider is not null;
        }
    }

    private static async Task<InMemoryAppConfigStore> StoreAsync(string? pat, string? owner = "acme", string? repo = "app")
    {
        var store = new InMemoryAppConfigStore();
        if (pat is not null) { await store.SetAsync(AppConfigKeys.GithubPat, pat); }
        if (owner is not null) { await store.SetAsync(AppConfigKeys.GithubRepoOwner, owner); }
        if (repo is not null) { await store.SetAsync(AppConfigKeys.GithubRepoName, repo); }
        return store;
    }

    private static async Task<GitHubTestResult> RunAsync(GitHubTestRequest? body, IAppConfigStore store, ISourceProvider? provider)
    {
        var result = await SettingsEndpoints.TestGitHubAsync(body, store, new Resolver(provider), null, TestContext.Current.CancellationToken);
        return result.ShouldBeOfType<Ok<GitHubTestResult>>().Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task Test_SavedToken_NothingTyped_ValidatesSavedRepoWithSavedToken()
    {
        var provider = Substitute.For<ISourceProvider>();
        provider.ValidateAsync(Arg.Any<WorkspaceDescriptor>(), Arg.Any<CancellationToken>()).Returns(RepoValidation.Success("main"));

        var result = await RunAsync(new GitHubTestRequest(), await StoreAsync(SavedPat), provider);

        result.Ok.ShouldBeTrue();
        result.Message.ShouldBe("acme/app reachable — default branch main.");
        await provider.Received(1).ValidateAsync(
            Arg.Is<WorkspaceDescriptor>(d => d.AccessToken == SavedPat && d.Owner == "acme" && d.Repo == "app"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_TypedTokenAndRepo_WinOverSaved()
    {
        var provider = Substitute.For<ISourceProvider>();
        provider.ValidateAsync(Arg.Any<WorkspaceDescriptor>(), Arg.Any<CancellationToken>()).Returns(RepoValidation.Success("dev"));

        var result = await RunAsync(new GitHubTestRequest(TypedPat, "other", "repo2", "dev"), await StoreAsync(SavedPat), provider);

        result.Ok.ShouldBeTrue();
        await provider.Received(1).ValidateAsync(
            Arg.Is<WorkspaceDescriptor>(d => d.AccessToken == TypedPat && d.Owner == "other" && d.Repo == "repo2" && d.DefaultBranch == "dev"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_NoTokenAnywhere_SaysSoInPlainLanguage()
    {
        var provider = Substitute.For<ISourceProvider>();

        var result = await RunAsync(null, await StoreAsync(pat: null), provider);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("No token saved");
        await provider.DidNotReceiveWithAnyArgs().ValidateAsync(default!, default);
    }

    [Fact]
    public async Task Test_NoRepoCoordinates_ChecksTheTokenCanListRepos()
    {
        IReadOnlyList<RemoteRepo> repos =
        [
            new("acme", "a", "acme/a", "main", "https://github.com/acme/a.git", false),
            new("acme", "b", "acme/b", "main", "https://github.com/acme/b.git", true),
        ];
        var provider = Substitute.For<ISourceProvider>();
        provider.ListRepositoriesAsync(Arg.Any<ConnectionCredentials>(), Arg.Any<CancellationToken>()).Returns(repos);

        var result = await RunAsync(null, await StoreAsync(SavedPat, owner: null, repo: null), provider);

        result.Ok.ShouldBeTrue();
        result.Message.ShouldContain("2 repositories");
        await provider.Received(1).ListRepositoriesAsync(
            Arg.Is<ConnectionCredentials>(c => c.AccessToken == SavedPat), Arg.Any<CancellationToken>());
    }

    // A provider error that echoes the credential must not carry it to the browser.
    [Fact]
    public async Task Test_ProviderErrorEchoesToken_TokenIsScrubbed()
    {
        var provider = Substitute.For<ISourceProvider>();
        provider.ValidateAsync(Arg.Any<WorkspaceDescriptor>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException($"Bad credentials for {SavedPat}"));

        var result = await RunAsync(null, await StoreAsync(SavedPat), provider);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldNotContain(SavedPat);
        result.Message.ShouldContain("Bad credentials");
    }

    [Fact]
    public async Task Test_FailedValidation_ReturnsReasonWithoutToken()
    {
        var provider = Substitute.For<ISourceProvider>();
        provider.ValidateAsync(Arg.Any<WorkspaceDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(RepoValidation.Fail($"Not found ({SavedPat})"));

        var result = await RunAsync(null, await StoreAsync(SavedPat), provider);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldBe("Not found (••••)");
    }

    [Fact]
    public async Task Test_NoGitHubProvider_SaysUnavailable()
    {
        var result = await RunAsync(null, await StoreAsync(SavedPat), provider: null);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("not available");
    }
}
