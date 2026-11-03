// RBAC v2 (E1.2) — the ASP.NET Core side of the matrix: the "perm:*" policies the Identity module registers, the
// permission handler, the legacy "Member" policy, and the role-claim mapping hook (legacy roles today, IdP groups
// with E1.1 SSO).

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentOs.Modules.Identity;
using AgentOs.Modules.Identity.Authorization;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Identity;

public sealed class RbacAuthorizationTests
{
    private static ServiceProvider Services(params IRoleClaimMapper[] mappers)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (var m in mappers) { services.AddSingleton(m); }
        new IdentityModule().AddServices(services, new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal User(params string[] roles) => new(new ClaimsIdentity(
        roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim(ClaimNames.Subject, "u1")),
        "test", ClaimTypes.Name, ClaimTypes.Role));

    public static TheoryData<string, Permission> RoleByPermission()
    {
        var data = new TheoryData<string, Permission>();
        foreach (var role in UserRoles.Managed)
        {
            foreach (var p in PermissionMatrix.Each) { data.Add(role, p); }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RoleByPermission))]
    public async Task PermissionPolicy_AgreesWithTheMatrix(string role, Permission permission)
    {
        await using var sp = Services();
        var auth = sp.GetRequiredService<IAuthorizationService>();
        var result = await auth.AuthorizeAsync(User(role), PermissionMatrix.Policy(permission));
        result.Succeeded.ShouldBe(PermissionMatrix.For(role).Has(permission), $"{role} × {permission}");
    }

    [Fact]
    public async Task PermissionPolicy_AnonymousIsRefused_EvenWithARoleClaim()
    {
        await using var sp = Services();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")]));
        (await sp.GetRequiredService<IAuthorizationService>().AuthorizeAsync(anonymous, PermissionMatrix.Policy(Permission.ViewWork)))
            .Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData("viewer", true)]
    [InlineData("auditor", true)]
    [InlineData("member", true)]
    [InlineData("admin", true)]
    [InlineData("platform-admin", false)]
    public async Task MemberPolicy_IsAnyTenantRole(string role, bool expected)
    {
        await using var sp = Services();
        (await sp.GetRequiredService<IAuthorizationService>().AuthorizeAsync(User(role), "Member")).Succeeded.ShouldBe(expected);
    }

    [Fact]
    public async Task Transformation_MapsLegacyMember_OntoDeveloper()
    {
        await using var sp = Services();
        var transformed = await sp.GetRequiredService<IClaimsTransformation>().TransformAsync(User("member"));
        transformed.IsInRole("developer").ShouldBeTrue();
        transformed.IsInRole("approver").ShouldBeFalse();   // E1.5's gates never accepted a member
        transformed.CanApprove().ShouldBeFalse();
        transformed.IsInRole("member").ShouldBeTrue();
    }

    [Fact]
    public async Task Transformation_IsIdempotent_AndLeavesOtherUsersAlone()
    {
        await using var sp = Services();
        var t = sp.GetRequiredService<IClaimsTransformation>();
        var once = await t.TransformAsync(User("member"));
        var twice = await t.TransformAsync(once);
        twice.FindAll(ClaimTypes.Role).Count(c => c.Value == "developer").ShouldBe(1);

        var viewer = User("viewer");
        (await t.TransformAsync(viewer)).ShouldBeSameAs(viewer);
    }

    [Fact]
    public async Task Transformation_RunsRegisteredMappers_TheE11GroupHook()
    {
        // What E1.1 SSO will register: IdP group claims → AgentOS.NET roles. Unknown role names are dropped.
        await using var sp = Services(new GroupMapper());
        var user = User();
        ((ClaimsIdentity)user.Identity!).AddClaim(new Claim("groups", "eng-approvers"));
        var transformed = await sp.GetRequiredService<IClaimsTransformation>().TransformAsync(user);
        transformed.IsInRole("approver").ShouldBeTrue();
        transformed.IsInRole("superuser").ShouldBeFalse();
        transformed.Can(Permission.ApproveWork).ShouldBeTrue();
        user.IsInRole("approver").ShouldBeFalse();   // the original principal is never mutated
    }

    [Fact]
    public async Task Transformation_SkipsAnonymous()
    {
        await using var sp = Services(new GroupMapper());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim("groups", "eng-approvers")]));
        (await sp.GetRequiredService<IClaimsTransformation>().TransformAsync(anonymous)).ShouldBeSameAs(anonymous);
    }

    private sealed class GroupMapper : IRoleClaimMapper
    {
        private static readonly string[] ApproverGroup = ["approver", "superuser"];

        public IEnumerable<string> MapRoles(ClaimsPrincipal user) =>
            user.FindAll("groups").SelectMany(g => g.Value switch
            {
                "eng-approvers" => ApproverGroup,
                _ => [],
            });
    }
}
