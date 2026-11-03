// Repo safety (E1.5, GAP-18) against a mocked GitHub (an NSubstitute IGitHubClient answering with real Octokit models
// deserialized from API-shaped JSON): pull requests open as drafts when the policy says so, a pull request is never
// opened from — and a merge never made from — the default branch, a draft is marked ready right before a merge, and a
// merge the base branch's protection would refuse (required reviews, code owners, required checks, GitHub's "blocked")
// is refused without calling the merge API.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using Octokit;
using Octokit.Internal;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Integration;

public sealed class GitHubRepoSafetyTests
{
    private const string Owner = "acme";
    private const string Repo = "shop";

    private static readonly SimpleJsonSerializer Json = new();

    private static T Model<T>(string json) => Json.Deserialize<T>(json);

    private static WorkspaceDescriptor Workspace(string defaultBranch = "main") =>
        new(Guid.NewGuid(), "t1", SourceProviderKind.GitHub, Owner, Repo, null, defaultBranch, "pat");

    // One mocked GitHub per test (xUnit builds a new instance for each), answering with real Octokit models.
    private IGitHubClient Client { get; } = Substitute.For<IGitHubClient>();
    private List<string> MarkedReady { get; } = [];
    private NewPullRequest? Opened { get; set; }

    private GitHubPrCreationService Service() =>
        new(NullLogger<GitHubPrCreationService>.Instance, (_, _) => Client, (_, nodeId, _) =>
        {
            MarkedReady.Add(nodeId);
            return Task.CompletedTask;
        });

    public GitHubRepoSafetyTests()
    {
        Client.PullRequest.Create(Owner, Repo, Arg.Do<NewPullRequest>(p => Opened = p))
            .Returns(_ => Model<PullRequest>("""{"number":12,"html_url":"https://github.com/acme/shop/pull/12","draft":true}"""));
        Client.Repository.Get(Owner, Repo).Returns(Model<Repository>("""{"default_branch":"main"}"""));
        Client.PullRequest.Review.GetAll(Owner, Repo, Arg.Any<int>()).Returns(new List<PullRequestReview>());
        Client.Check.Run.GetAllForReference(Owner, Repo, Arg.Any<string>()).Returns(Model<CheckRunsResponse>("""{"total_count":0,"check_runs":[]}"""));
        Client.Repository.Status.GetCombined(Owner, Repo, Arg.Any<string>()).Returns(Model<CombinedCommitStatus>("""{"state":"success","statuses":[]}"""));
        Client.PullRequest.Files(Owner, Repo, Arg.Any<int>()).Returns(new List<PullRequestFile>
        {
            Model<PullRequestFile>("""{"filename":"docs/readme.md"}"""),
        });
        Client.Repository.Content.GetAllContentsByRef(Owner, Repo, Arg.Any<string>(), Arg.Any<string>())
            .Throws(new NotFoundException("no CODEOWNERS", System.Net.HttpStatusCode.NotFound));
        Client.Repository.Branch.GetBranchProtection(Owner, Repo, Arg.Any<string>())
            .Throws(new NotFoundException("Branch not protected", System.Net.HttpStatusCode.NotFound));
        Client.PullRequest.Merge(Owner, Repo, Arg.Any<int>(), Arg.Any<MergePullRequest>())
            .Returns(Model<PullRequestMerge>("""{"merged":true,"message":"merged","sha":"m1"}"""));
    }

    private static PullRequest Pr(int number, string head = "agentos/issue-5", bool draft = false, string state = "clean") =>
        Model<PullRequest>(
            $$$"""{"number":{{{number}}},"node_id":"PR_node","draft":{{{(draft ? "true" : "false")}}},"mergeable":true,"mergeable_state":"{{{state}}}","head":{"ref":"{{{head}}}","sha":"abc"},"base":{"ref":"main","sha":"def"},"user":{"login":"agentos-bot"}}""");

    private void PullRequest(int number, string head = "agentos/issue-5", bool draft = false, string state = "clean") =>
        Client.PullRequest.Get(Owner, Repo, number).Returns(Pr(number, head, draft, state));

    private void Protection(string json) =>
        // Configure(): re-configuring a call that throws must not run it.
        Client.Repository.Branch.Configure().GetBranchProtection(Owner, Repo, "main").Returns(Model<BranchProtectionSettings>(json));

    private void Reviews(params (string Login, string State)[] reviews)
    {
        var list = new List<PullRequestReview>();
        var at = 0;
        foreach (var (login, state) in reviews)
        {
            list.Add(Model<PullRequestReview>(
                $$"""{"id":{{++at}},"state":"{{state}}","user":{"login":"{{login}}"},"submitted_at":"2026-11-0{{at}}T10:00:00Z"}"""));
        }
        Client.PullRequest.Review.GetAll(Owner, Repo, Arg.Any<int>()).Returns(list);
    }

    private void CodeOwners(string text) =>
        Client.Repository.Content.Configure().GetAllContentsByRef(Owner, Repo, ".github/CODEOWNERS", "main")
            .Returns(new List<RepositoryContent> { Model<RepositoryContent>($$"""{"name":"CODEOWNERS","path":".github/CODEOWNERS","type":"file","encoding":"base64","content":"{{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))}}"}""") });

    // ── Opening pull requests ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateFromBranch_OpensADraft_OnlyWhenThePolicySaysSo(bool draft)
    {

        var result = await Service().CreateFromBranchAsync(Workspace(), "agentos/issue-5", "Add dark mode", "body", draft);

        result.Ok.ShouldBeTrue();
        Opened.ShouldNotBeNull();
        Opened.Draft.ShouldBe(draft);
        Opened.Head.ShouldBe("agentos/issue-5");
        Opened.Base.ShouldBe("main");
    }

    [Theory]
    [InlineData("main")]
    [InlineData("refs/heads/main")]
    public async Task CreateFromBranch_NeverFromTheDefaultBranch(string branch)
    {

        var result = await Service().CreateFromBranchAsync(Workspace(), branch, "t", "b", draft: false);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("default branch");
        await Client.PullRequest.DidNotReceiveWithAnyArgs().Create(default!, default!, default!);
    }

    [Fact]
    public async Task ProviderWithoutDrafts_RefusesInsteadOfOpeningAReadyOne()
    {
        IPrCreationService plain = new NoDraftProvider();

        var result = await plain.CreateFromBranchAsync(Workspace(), "agentos/1", "t", "b", draft: true);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("draft");
    }

    private sealed class NoDraftProvider : IPrCreationService
    {
        public Task<PrCreationResult> CreateFromBranchAsync(WorkspaceDescriptor workspace, string branch, string title, string body, CancellationToken ct = default) =>
            Task.FromResult(new PrCreationResult(true, 1, "u", null));
    }

    // ── Merging ──

    [Fact]
    public async Task Merge_Unprotected_Merges()
    {
        PullRequest(5);

        (await Service().MergeAsync(Workspace(), 5)).ShouldBeNull();

        await Client.PullRequest.Received(1).Merge(Owner, Repo, 5, Arg.Any<MergePullRequest>());
    }

    [Fact]
    public async Task Merge_RefusedWhenTheProtectionWouldRefuse_RequiredReviews()
    {
        PullRequest(5);
        Protection("""{"required_pull_request_reviews":{"required_approving_review_count":1,"require_code_owner_reviews":false},"required_status_checks":{"strict":false,"contexts":[]}}""");

        var error = await Service().MergeAsync(Workspace(), 5);

        error.ShouldNotBeNull().ShouldStartWith("Refusing to merge");
        error.ShouldContain("needs 1 approving review(s), it has 0");
        await Client.PullRequest.DidNotReceiveWithAnyArgs().Merge(default!, default!, default, default!);
    }

    [Fact]
    public async Task Merge_RefusedWithoutACodeOwnersApproval_AndAllowedWithIt()
    {
        PullRequest(5);
        Protection("""{"required_pull_request_reviews":{"required_approving_review_count":1,"require_code_owner_reviews":true}}""");
        CodeOwners("/docs/ @alice\n");
        Reviews(("bob", "APPROVED"));

        (await Service().MergeAsync(Workspace(), 5)).ShouldNotBeNull().ShouldContain("a code owner must approve docs/readme.md (@alice)");
        await Client.PullRequest.DidNotReceiveWithAnyArgs().Merge(default!, default!, default, default!);

        Reviews(("bob", "APPROVED"), ("alice", "APPROVED"));
        (await Service().MergeAsync(Workspace(), 5)).ShouldBeNull();
        await Client.PullRequest.Received(1).Merge(Owner, Repo, 5, Arg.Any<MergePullRequest>());
    }

    [Fact]
    public async Task Merge_RefusedWhenARequiredCheckHasNotPassed()
    {
        PullRequest(5);
        Protection("""{"required_status_checks":{"strict":true,"contexts":["build"]}}""");

        (await Service().MergeAsync(Workspace(), 5)).ShouldNotBeNull().ShouldContain("the required check 'build' has not reported");
        await Client.PullRequest.DidNotReceiveWithAnyArgs().Merge(default!, default!, default, default!);
    }

    [Fact]
    public async Task Merge_RefusedByARulesetRule_ReadFromTheRulesApi()
    {
        PullRequest(5);
        // The JSON GET /repos/{o}/{r}/rules/branches/main returns, through Octokit's own (snake_case) deserializer.
        var rules = Model<List<AgentOs.Modules.Integration.Governance.GitHubBranchRule>>(
            """[{"type":"pull_request","parameters":{"required_approving_review_count":2,"require_code_owner_review":false}},{"type":"required_status_checks","parameters":{"required_status_checks":[{"context":"ci/build"}]}}]""");
        var response = Substitute.For<IApiResponse<List<AgentOs.Modules.Integration.Governance.GitHubBranchRule>>>();
        response.Body.Returns(rules);
        Client.Connection.Get<List<AgentOs.Modules.Integration.Governance.GitHubBranchRule>>(
            Arg.Is<Uri>(u => u.ToString() == "repos/acme/shop/rules/branches/main"), Arg.Any<IDictionary<string, string>>()).Returns(response);

        var error = await Service().MergeAsync(Workspace(), 5);

        error.ShouldNotBeNull().ShouldContain("needs 2 approving review(s)");
        error.ShouldContain("the required check 'ci/build' has not reported");
        await Client.PullRequest.DidNotReceiveWithAnyArgs().Merge(default!, default!, default, default!);
    }

    [Fact]
    public async Task Merge_RefusedWhenGitHubSaysBlocked_EvenIfTheTokenCouldBypass()
    {
        PullRequest(5, state: "blocked");

        (await Service().MergeAsync(Workspace(), 5)).ShouldNotBeNull().ShouldContain("blocked by the branch's protection");
        await Client.PullRequest.DidNotReceiveWithAnyArgs().Merge(default!, default!, default, default!);
    }

    [Fact]
    public async Task Merge_NeverFromTheDefaultBranch()
    {
        PullRequest(5, head: "main");

        (await Service().MergeAsync(Workspace(), 5)).ShouldNotBeNull().ShouldContain("default branch");
        await Client.PullRequest.DidNotReceiveWithAnyArgs().Merge(default!, default!, default, default!);
        await Client.Git.Reference.DidNotReceiveWithAnyArgs().Delete(default!, default!, default!);
    }

    [Fact]
    public async Task Merge_ADraft_IsMarkedReadyFirst()
    {
        // The first read is the draft; after the mutation GitHub reads it ready.
        Client.PullRequest.Get(Owner, Repo, 5).Returns(Pr(5, draft: true), Pr(5, draft: false));

        (await Service().MergeAsync(Workspace(), 5)).ShouldBeNull();

        MarkedReady.ShouldBe(["PR_node"]);
        await Client.PullRequest.Received(1).Merge(Owner, Repo, 5, Arg.Any<MergePullRequest>());
    }
}
