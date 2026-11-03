// Repo safety (E1.5, GAP-18), pure: the default branch is never a change's branch, and a merge the base branch's
// protection would refuse is refused — required reviews, requested changes, code owners (CODEOWNERS, GitHub semantics:
// last match wins), required checks, GitHub's own "blocked" verdict, drafts and conflicts.

using System.Collections.Generic;
using AgentOs.Domain.Governance;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Governance;

public sealed class RepoSafetyTests
{
    [Theory]
    [InlineData("main", true)]
    [InlineData("refs/heads/main", true)]
    [InlineData(" main ", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("agentos/issue-12", false)]
    [InlineData("Main", false)]
    public void IsDefaultBranch(string? branch, bool expected) =>
        RepoSafety.IsDefaultBranch(branch, "main", null).ShouldBe(expected);

    [Fact]
    public void RefuseDefaultBranch_SaysWhy()
    {
        RepoSafety.RefuseDefaultBranch("develop", "develop").ShouldNotBeNull().ShouldContain("default branch 'develop'");
        RepoSafety.RefuseDefaultBranch("agentos/1", "main").ShouldBeNull();
    }

    private static MergeFacts Facts(
        BranchProtectionFacts? protection = null,
        IReadOnlyList<ReviewFact>? reviews = null,
        IReadOnlyList<CheckFact>? checks = null,
        IReadOnlyList<string>? files = null,
        CodeOwners? owners = null,
        IReadOnlySet<string>? approvedOwners = null,
        string head = "agentos/issue-12",
        bool draft = false,
        string? state = "clean") =>
        new(head, "main", "main", draft, state, "agentos-bot", reviews ?? [], checks ?? [], files ?? ["src/app.ts"],
            protection ?? BranchProtectionFacts.Unprotected, owners, approvedOwners ?? new HashSet<string>());

    [Fact]
    public void Unprotected_CleanMerge_IsAllowed() =>
        MergeProtection.Refusal(Facts()).ShouldBeNull();

    [Fact]
    public void FromTheDefaultBranch_IsRefused() =>
        MergeProtection.Refusal(Facts(head: "main")).ShouldNotBeNull().ShouldContain("default branch");

    [Fact]
    public void Draft_IsRefused() =>
        MergeProtection.Refusal(Facts(draft: true)).ShouldNotBeNull().ShouldContain("draft");

    [Theory]
    [InlineData("blocked", "blocked by the branch's protection")]
    [InlineData("dirty", "conflicts")]
    public void GitHubsOwnVerdict_IsHonoured(string state, string expected) =>
        MergeProtection.Refusal(Facts(state: state)).ShouldNotBeNull().ShouldContain(expected);

    [Fact]
    public void RequiredApprovals_AreCounted_WithoutTheAuthor()
    {
        var protection = new BranchProtectionFacts(true, true, RequiredApprovals: 2, false, [], false);

        MergeProtection.Evaluate(Facts(protection, [new("alice", ReviewState.Approved), new("agentos-bot", ReviewState.Approved)]))
            .ShouldContain("the branch needs 2 approving review(s), it has 1");
        MergeProtection.Evaluate(Facts(protection, [new("alice", ReviewState.Approved), new("bob", ReviewState.Approved)])).ShouldBeEmpty();
    }

    [Fact]
    public void RequestedChanges_BlockAProtectedBranch()
    {
        var protection = new BranchProtectionFacts(true, true, 1, false, [], false);

        MergeProtection.Evaluate(Facts(protection, [new("alice", ReviewState.Approved), new("bob", ReviewState.ChangesRequested)]))
            .ShouldContain("a reviewer requested changes");
    }

    [Fact]
    public void CodeOwnerReviews_NeedAnOwnerOfEveryChangedFile()
    {
        var owners = CodeOwners.Parse("* @org/core\n/docs/ @alice\n");
        var protection = new BranchProtectionFacts(true, true, 1, RequireCodeOwnerReviews: true, [], false);
        var approved = new[] { new ReviewFact("bob", ReviewState.Approved) };

        var refused = MergeProtection.Refusal(Facts(protection, approved, files: ["docs/readme.md", "src/a.ts"], owners: owners,
            approvedOwners: new HashSet<string> { "@org/core" }));
        refused.ShouldNotBeNull().ShouldContain("a code owner must approve docs/readme.md (@alice)");

        MergeProtection.Refusal(Facts(protection, approved, files: ["docs/readme.md", "src/a.ts"], owners: owners,
            approvedOwners: new HashSet<string> { "@org/core", "@alice" })).ShouldBeNull();
    }

    [Fact]
    public void UnreadableProtection_StillEnforcesCodeOwners_FailClosed()
    {
        var unknown = new BranchProtectionFacts(true, DetailsKnown: false, 0, false, [], false);

        MergeProtection.Refusal(Facts(unknown, owners: CodeOwners.Parse("*.ts @alice"))).ShouldNotBeNull().ShouldContain("code owner");
        MergeProtection.Refusal(Facts(BranchProtectionFacts.Unprotected, owners: CodeOwners.Parse("*.ts @alice"))).ShouldBeNull();
    }

    [Fact]
    public void RequiredChecks_MustHaveReportedAndPassed()
    {
        var protection = new BranchProtectionFacts(true, true, 0, false, ["build", "e2e"], false);

        var reasons = MergeProtection.Evaluate(Facts(protection, checks: [new("build", true), new("BUILD", false)]));
        reasons.ShouldContain("the required check 'build' has not passed");
        reasons.ShouldContain("the required check 'e2e' has not reported");
        MergeProtection.Evaluate(Facts(protection, checks: [new("build", true), new("e2e", true)])).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("*.js @a", "lib/deep/x.js", "@a")]
    [InlineData("/build/ @b", "build/logs/x.txt", "@b")]
    [InlineData("/build/ @b", "src/build/x.txt", "")]
    [InlineData("apps/ @c", "src/apps/x.cs", "@c")]
    [InlineData("docs/* @d", "docs/a.md", "@d")]
    [InlineData("docs/* @d", "docs/sub/a.md", "")]
    [InlineData("**/logs @e", "deep/a/logs/x.log", "@e")]
    [InlineData("/src/**/test/ @f", "src/a/b/test/x.cs", "@f")]
    public void CodeOwners_FollowsGitHubPatterns(string line, string path, string expected)
    {
        var owners = CodeOwners.Parse(line).OwnersOf(path);
        string.Join(' ', owners).ShouldBe(expected);
    }

    [Fact]
    public void CodeOwners_LastMatchWins_AndAPatternWithoutOwnersClearsThem()
    {
        var owners = CodeOwners.Parse("""
            # comment
            *       @org/everyone
            *.md    @Docs-Team    # trailing comment
            /vendor/
            """);

        owners.OwnersOf("README.md").ShouldBe(["@docs-team"]);
        owners.OwnersOf("src/app.cs").ShouldBe(["@org/everyone"]);
        owners.OwnersOf("vendor/lib.cs").ShouldBeEmpty();
        owners.AllOwners().ShouldBe(new HashSet<string> { "@org/everyone", "@docs-team" }, ignoreOrder: true);
    }
}
