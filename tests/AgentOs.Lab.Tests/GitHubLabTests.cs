// Real GitHub, real token, real sandbox repo (hoangsnowy/agentos-lab by default). Proves the production
// GitHubSourceProvider against the live API: token + repo validation, repo listing, the README context the
// Requirement agent is grounded on, ticket bodies, idempotent label sync, and — when a Projects board is
// configured — reading and creating board tickets. Anything a test creates it deletes/closes in a finally.

using System;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration.Sources;
using Octokit;
using Shouldly;
using Xunit;

namespace AgentOs.Lab.Tests;

public sealed class GitHubLabTests
{
    private const string Tenant = "lab";
    private readonly GitHubSourceProvider _github = new();

    private static WorkspaceDescriptor Repo() => new(
        Guid.NewGuid(), Tenant, SourceProviderKind.GitHub, LabConfig.GitHubOwner, LabConfig.GitHubRepo,
        Project: null, DefaultBranch: "main", AccessToken: LabConfig.GitHubToken!);

    private static GitHubClient Octo() => new(new ProductHeaderValue("agentos-lab-tests"))
    {
        Credentials = new Credentials(LabConfig.GitHubToken),
    };

    private static void SkipUnlessConfigured()
    {
        if (LabConfig.GitHubSkipReason is { } why) { Assert.Skip(why); }
    }

    [Fact]
    public async Task Validate_TokenReachesTheLabRepo_AndResolvesDefaultBranch()
    {
        SkipUnlessConfigured();
        var v = await _github.ValidateAsync(Repo());
        v.Ok.ShouldBeTrue(v.Error);
        v.DefaultBranch.ShouldBe("main");
    }

    [Fact]
    public async Task ListRepositories_IncludesTheLabRepo()
    {
        SkipUnlessConfigured();
        var repos = await _github.ListRepositoriesAsync(new ConnectionCredentials(SourceProviderKind.GitHub, LabConfig.GitHubToken!, LabConfig.GitHubOwner));
        repos.ShouldContain(r => r.Name == LabConfig.GitHubRepo && r.Private);
    }

    [Fact]
    public async Task ReadRepoContext_ReturnsReadmeAndLayout_ForGroundingTheRequirementAgent()
    {
        SkipUnlessConfigured();
        var ctx = await _github.ReadRepoContextAsync(Repo());
        ctx.Readme.ShouldContain("AgentOS lab");
        ctx.TopLevelPaths.ShouldContain(p => p.StartsWith("src", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadRepoContext_ReadsTheRepoAgentInstructions()
    {
        // E0.7: the sandbox repo ships an AGENTS.md; the real GitHub read must find it so every agent follows it.
        SkipUnlessConfigured();
        var ctx = await _github.ReadRepoContextAsync(Repo());
        var agents = ctx.Instructions.ShouldHaveSingleItem();
        agents.Path.ShouldBe("AGENTS.md");
        agents.Content.ShouldContain("xUnit v3 + Shouldly");
        RepoInstructions.ToPromptBlock(ctx.Instructions).ShouldContain("<repo_guidance path=\"AGENTS.md\">");
    }

    [Fact]
    public async Task ReadTicketBody_ReturnsTheLiveIssueDescription()
    {
        SkipUnlessConfigured();
        var body = await _github.ReadTicketBodyAsync(Repo(), 1);
        body.ShouldNotBeNull();
        body.ShouldContain("ship free");
    }

    [Fact]
    public async Task EnsureLabels_IsIdempotent_AndCleansUp()
    {
        SkipUnlessConfigured();
        var name = "lab-test-" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            var first = await _github.EnsureLabelsAsync(Repo(), [new LabelSpec(name, "0e8a16", "AgentOS lab test")]);
            first.Created.ShouldContain(name);
            var second = await _github.EnsureLabelsAsync(Repo(), [new LabelSpec(name, "0e8a16")]);
            second.Created.ShouldBeEmpty();
            second.Existing.ShouldContain(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { await Octo().Issue.Labels.Delete(LabConfig.GitHubOwner, LabConfig.GitHubRepo, name); }
            catch (NotFoundException) { /* never created */ }
        }
    }

    [Fact]
    public async Task Board_ReadAndCreateTickets_OnTheLabProject()
    {
        SkipUnlessConfigured();
        var number = LabConfig.GitHubProjectNumber ?? 0;
        if (number == 0)
        {
            Assert.Skip("Set Lab:GitHub:ProjectNumber to the lab's GitHub Projects board (see docs/LAB.md).");
        }

        var boards = await _github.ListBoardsAsync(new ConnectionCredentials(SourceProviderKind.GitHub, LabConfig.GitHubToken!, LabConfig.GitHubOwner));
        var summary = boards.FirstOrDefault(b => b.Number == number);
        summary.ShouldNotBeNull($"project #{number} not visible — the token needs the 'project' scope");

        var board = new BoardDescriptor(Guid.NewGuid(), Tenant, SourceProviderKind.GitHub, LabConfig.GitHubOwner,
            summary.Scope, number, summary.NodeId, LabConfig.GitHubToken!);
        (await _github.ValidateBoardAsync(board)).Ok.ShouldBeTrue();

        var created = await _github.CreateTicketsAsync(board, Repo(),
            [new TicketDraft("[lab] decomposition smoke " + DateTimeOffset.UtcNow.ToString("u"), "Created by GitHubLabTests; closed automatically.", [], AiReady: false)]);
        try
        {
            created.Count.ShouldBe(1);
            var tickets = await _github.ReadBoardTicketsAsync(board);
            tickets.Items.ShouldContain(t => t.Title == created[0].Title);
        }
        finally
        {
            foreach (var t in created)
            {
                await Octo().Issue.Update(LabConfig.GitHubOwner, LabConfig.GitHubRepo, t.Number, new IssueUpdate { State = ItemState.Closed });
            }
        }
    }
}
