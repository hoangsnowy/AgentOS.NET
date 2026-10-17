// A1 — the PR-target guards that fire BEFORE any GitHub network call: the Settings-token fallback rejects an
// unconfigured tenant; the workspace overload is GitHub-only and null-checked. (The happy path hits Octokit,
// which isn't mockable, so it's covered by the full-stack verification, not here.)

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain;
using AgentOs.Domain.Code;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Qa;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Testing;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration;
using AgentOs.Modules.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Integration;

public sealed class GitHubPrServiceGuardTests
{
    private static GitHubPrService Sut(IRuntimeOverrides? overrides = null)
        => new(overrides ?? Substitute.For<IRuntimeOverrides>(), NullLogger<GitHubPrService>.Instance);

    private static PipelineResult Result(string codingProvider = "p")
    {
        var zero = new AgentMetrics("p", "m", 0, 0, 0m, TimeSpan.Zero);
        var coding = zero with { Provider = codingProvider };
        return new PipelineResult(
            new UserStory("s", 1, "en"),
            new RequirementSpec("T", "S", [], [], [], [], [], [], zero),
            new CodeArtifact("n", "a", [], null, coding),
            new TestArtifact("x", [], 0, 0, 0, 0, zero),
            [new QaReport(1, true, false, [], [], zero)],
            PipelineStatus.Done, zero);
    }

    [Fact]
    public async Task OpenPr_LegacyWithNoOverrides_ThrowsNotConfigured()
    {
        // Default substitute → all GitHub override fields null → the fallback path rejects up front.
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Sut().OpenPrAsync(Result(), "title", "body", CancellationToken.None));
        ex.Message.ShouldContain("not configured");
    }

    [Fact]
    public async Task OpenPr_AzureDevOpsWorkspace_ThrowsGitHubOnly()
    {
        var ws = new WorkspaceDescriptor(Guid.NewGuid(), "t", SourceProviderKind.AzureDevOps, "org", "repo", "proj", "main", "tok");
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Sut().OpenPrAsync(Result(), ws, "title", "body", CancellationToken.None));
        ex.Message.ShouldContain("GitHub-only");
    }

    [Fact]
    public async Task OpenPr_NullWorkspace_Throws()
        => await Should.ThrowAsync<ArgumentNullException>(
            () => Sut().OpenPrAsync(Result(), null!, "title", "body", CancellationToken.None));

    [Fact]
    public async Task OpenPr_OfflineCannedResult_RefusesBeforeAnyNetworkCall()
    {
        // A GitHub workspace that would otherwise pass every guard — only the canned output stops it.
        var ws = new WorkspaceDescriptor(Guid.NewGuid(), "t", SourceProviderKind.GitHub, "owner", "repo", null, "main", "tok");
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Sut().OpenPrAsync(Result(codingProvider: "Offline"), ws, "title", "body", CancellationToken.None));
        ex.Message.ShouldContain("offline canned client");
    }
}
