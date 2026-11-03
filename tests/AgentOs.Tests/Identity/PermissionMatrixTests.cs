// RBAC v2 (E1.2) — the permission matrix, cell by cell. The expected table below is the spec: every role × every
// permission is asserted, so a change to what a role may do is a deliberate edit here AND in PermissionMatrix.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using AgentOs.SharedKernel.Identity;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Identity;

public sealed class PermissionMatrixTests
{
    private const Permission V = Permission.ViewWork;
    private const Permission R = Permission.RunAgents;
    private const Permission A = Permission.ApproveWork;
    private const Permission W = Permission.ManageWorkspaces;
    private const Permission M = Permission.ManageMembers;
    private const Permission S = Permission.ManageSettings;
    private const Permission T = Permission.ManageApiTokens;
    private const Permission Au = Permission.ViewAudit;
    private const Permission X = Permission.ExportData;

    // The spec. Rows = roles, values = exactly the permissions the role grants.
    private static readonly Dictionary<string, Permission[]> Expected = new(StringComparer.Ordinal)
    {
        ["viewer"] = [V],
        ["developer"] = [V, R],
        ["approver"] = [V, A],
        ["auditor"] = [V, Au],
        ["admin"] = [V, R, A, W, M, S, T, Au, X],
        ["member"] = [V, R, W],             // legacy alias: everything a pre-E1.2 member could do (no E1.5 approvals)
        ["platform-admin"] = [],            // orthogonal: platform-wide settings, nothing tenant-scoped
        ["unknown-role"] = [],
    };

    public static TheoryData<string, Permission, bool> EveryCell()
    {
        var data = new TheoryData<string, Permission, bool>();
        foreach (var (role, granted) in Expected)
        {
            foreach (var permission in PermissionMatrix.Each)
            {
                data.Add(role, permission, granted.Contains(permission));
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCell))]
    public void For_EveryRoleAndPermission_MatchesTheSpec(string role, Permission permission, bool expected)
        => PermissionMatrix.For(role).Has(permission).ShouldBe(expected, $"{role} × {permission}");

    [Fact]
    public void Each_ListsEveryDefinedPermissionOnce_AndAllIsTheirUnion()
    {
        var defined = Enum.GetValues<Permission>().Where(p => p != Permission.None).ToArray();
        PermissionMatrix.Each.ShouldBe(defined, ignoreOrder: true);
        PermissionMatrix.Each.Distinct().Count().ShouldBe(PermissionMatrix.Each.Count);
        PermissionMatrix.Each.Aggregate(Permission.None, (acc, p) => acc | p).ShouldBe(PermissionMatrix.All);
        // Single flags only — a combined value in Each would make "has X" checks ambiguous.
        PermissionMatrix.Each.ShouldAllBe(p => ((long)p & ((long)p - 1)) == 0);
    }

    [Fact]
    public void Matrix_CoversExactlyTheManagedRoles()
        => PermissionMatrix.Roles.Keys.ShouldBe(UserRoles.Managed, ignoreOrder: true);

    [Fact]
    public void Has_NoneIsNeverGranted_AndCombinationsNeedEveryFlag()
    {
        PermissionMatrix.All.Has(Permission.None).ShouldBeFalse();
        PermissionMatrix.For("developer").Has(R | V).ShouldBeTrue();
        PermissionMatrix.For("developer").Has(R | A).ShouldBeFalse();
    }

    [Fact]
    public void For_RolesAreAdditive()
    {
        var both = PermissionMatrix.For(["developer", "approver"]);
        both.ShouldBe(V | R | A);
        PermissionMatrix.For(["viewer", "auditor", "nonsense"]).ShouldBe(V | Au);
        PermissionMatrix.For(Array.Empty<string>()).ShouldBe(Permission.None);
    }

    [Fact]
    public void For_IsCaseSensitive_LikeKeycloakRoleNames()
        => PermissionMatrix.For("Admin").ShouldBe(Permission.None);

    [Fact]
    public void LegacyMember_KeepsEverythingADeveloperHas_ButApprovesNothing()
    {
        var member = PermissionMatrix.For("member");
        member.Has(PermissionMatrix.For("developer")).ShouldBeTrue();
        member.Has(Permission.ManageWorkspaces).ShouldBeTrue();
        member.Has(Permission.ApproveWork).ShouldBeFalse();
        member.Has(Permission.ManageMembers).ShouldBeFalse();
    }

    // ── Workspace (board) scope ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ForWorkspace_OpenBoard_TenantRolesApply()
        => PermissionMatrix.ForWorkspace(["developer"], restricted: false, membershipRole: null).ShouldBe(V | R);

    [Fact]
    public void ForWorkspace_RestrictedBoard_WithoutMembership_GrantsNothingToWorkspaceRoles()
    {
        PermissionMatrix.ForWorkspace(["developer", "approver", "viewer", "member"], restricted: true, membershipRole: null)
            .ShouldBe(Permission.None);
    }

    [Fact]
    public void ForWorkspace_RestrictedBoard_MembershipRoleReplacesTenantRole()
    {
        // Tenant developer, viewer on this board → read only here.
        PermissionMatrix.ForWorkspace(["developer"], restricted: true, membershipRole: "viewer").ShouldBe(V);
        // Tenant viewer, developer on this board → may run agents here.
        PermissionMatrix.ForWorkspace(["viewer"], restricted: true, membershipRole: "developer").ShouldBe(V | R);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("auditor")]
    public void ForWorkspace_TenantWideRoles_ApplyOnEveryBoard(string role)
        => PermissionMatrix.ForWorkspace([role], restricted: true, membershipRole: null).ShouldBe(PermissionMatrix.For(role));

    [Theory]
    [InlineData("admin")]
    [InlineData("auditor")]
    [InlineData("member")]
    [InlineData("platform-admin")]
    public void ForWorkspace_AMembershipCanOnlyCarryAWorkspaceRole(string role)
        => PermissionMatrix.ForWorkspace(["viewer"], restricted: true, membershipRole: role).ShouldBe(Permission.None);

    [Fact]
    public void WorkspaceAccessMap_AnswersPerBoard()
    {
        var open = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var rows = new[]
        {
            new WorkspaceMember(mine, "u1", "developer", null, DateTimeOffset.UnixEpoch),
            new WorkspaceMember(theirs, "u2", "approver", null, DateTimeOffset.UnixEpoch),
        };
        var map = WorkspaceAccessMap.Build(["viewer"], "u1", rows);

        map.TenantPermissions.ShouldBe(V);
        map.CanSee(open).ShouldBeTrue();                       // open board → tenant viewer reads
        map.Can(open, R).ShouldBeFalse();
        map.Can(mine, R).ShouldBeTrue();                        // membership elevates on that board only
        map.CanSee(theirs).ShouldBeFalse();                     // restricted, not a member
        map.IsRestricted(open).ShouldBeFalse();
        map.IsRestricted(theirs).ShouldBeTrue();
        map.MembershipRole(mine).ShouldBe("developer");
        map.RolesOn(mine).ShouldBe(["developer"]);
    }

    [Fact]
    public void WorkspaceAccessMap_AnonymousUserIdMatchesNoMembership()
    {
        var board = Guid.NewGuid();
        var map = WorkspaceAccessMap.Build([], null, [new WorkspaceMember(board, "u1", "viewer", null, DateTimeOffset.UnixEpoch)]);
        map.CanSee(board).ShouldBeFalse();
    }

    // ── Names, policies, principals ──────────────────────────────────────────────────────────────

    [Fact]
    public void Policy_NamesAreUniqueAndPrefixed()
    {
        var names = PermissionMatrix.Each.Select(PermissionMatrix.Policy).ToArray();
        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Length);
        names.ShouldAllBe(n => n.StartsWith(PermissionMatrix.PolicyPrefix, StringComparison.Ordinal));
        PermissionMatrix.Policy(Permission.RunAgents).ShouldBe("perm:RunAgents");
    }

    [Fact]
    public void Assignable_IsTheFiveRoles_LeastToMostPrivileged()
    {
        UserRoles.Assignable.ShouldBe(["viewer", "developer", "approver", "auditor", "admin"]);
        UserRoles.WorkspaceAssignable.ShouldBe(["viewer", "developer", "approver"]);
        UserRoles.IsAssignable("member").ShouldBeFalse();
        UserRoles.IsAssignable(null).ShouldBeFalse();
        UserRoles.Assignable.ShouldAllBe(r => UserRoles.Label(r).Length > 0 && UserRoles.Describe(r).Length > 0);
    }

    [Fact]
    public void CanApprove_IsTheMatrixApproveWork_ForE15Gates()
    {
        static ClaimsPrincipal As(string role) => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
        As("approver").CanApprove().ShouldBeTrue();
        As("admin").CanApprove().ShouldBeTrue();
        foreach (var role in new[] { "viewer", "developer", "auditor", "member", "platform-admin" })
        {
            As(role).CanApprove().ShouldBeFalse(role);
        }
    }

    [Fact]
    public void Can_ReadsTheRoleClaims_AndAnonymousHoldsNothing()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "developer"), new Claim(ClaimTypes.Role, "auditor")], "test"));
        user.GetPermissions().ShouldBe(V | R | Au);
        user.Can(Permission.RunAgents).ShouldBeTrue();
        user.Can(Permission.ManageMembers).ShouldBeFalse();

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")]));
        anonymous.GetPermissions().ShouldBe(Permission.None);
    }
}
