// RBAC v2 (E1.2) — endpoint authorization per role, over real HTTP. An in-process TestServer hosts the real module
// endpoints (Tenants, Workspaces, Sessions, Settings) with the Identity module's real policies + handler, and a test
// authentication scheme that signs the caller in with the roles named in a header. Every case asserts the status the
// matrix implies: 401 anonymous, 403 when the role lacks the permission, anything else when it has it (the stubbed
// services behind an allowed call may still answer 400/404 — authorization already passed). Per-board isolation runs
// through the real WorkspaceAccess over the in-memory membership store.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Endpoints;
using AgentOs.Modules.Identity;
using AgentOs.Modules.Sessions.Endpoints;
using AgentOs.Modules.Sessions.Pairing;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Email;
using AgentOs.Modules.Tenants.Endpoints;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.Modules.Workspaces;
using AgentOs.Modules.Workspaces.Access;
using AgentOs.Modules.Workspaces.Endpoints;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.Modules.Workspaces.Security;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Identity;

public sealed class EndpointAuthorizationByRoleTests : IAsyncLifetime
{
    private const string Tenant = "acme";
    private static readonly Guid OpenBoard = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid RestrictedBoard = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private static readonly string[] ViewerOnly = ["viewer"];

    private WebApplication? _app;
    private HttpClient? _client;
    private readonly InMemoryWorkspaceMembershipStore _memberships = new(TimeProvider.System);
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services.AddAuthentication(HeaderAuth.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.SchemeName, _ => { });
        // The real policies, permission handler and claim-mapping hook.
        new IdentityModule().AddServices(builder.Services, new ConfigurationBuilder().Build());

        // Tenants
        builder.Services.AddSingleton(Substitute.For<ITenantsRepository>());
        var kc = Substitute.For<IKeycloakAdminClient>();
        kc.GetUserTenantAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Tenant);
        kc.ListUsersByTenantAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        builder.Services.AddSingleton(kc);
        builder.Services.AddSingleton(Substitute.For<IAuditLog>());
        builder.Services.AddSingleton(Substitute.For<IEmailSender>());
        builder.Services.AddSingleton(Substitute.For<ITenantSignupService>());
        builder.Services.AddSingleton<IWorkspaceMembershipStore>(_memberships);
        builder.Services.AddSingleton(new AgentOs.SharedKernel.Hosting.PublicOrigin("https://agentos.example.com", allowRequestFallback: false));
        // Workspaces
        var boards = Substitute.For<IWorkspaceRepository>();
        boards.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new WorkspaceEntity { Id = OpenBoard, TenantId = Tenant, Name = "open" },
            new WorkspaceEntity { Id = RestrictedBoard, TenantId = Tenant, Name = "restricted" },
        ]);
        boards.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new WorkspaceEntity { Id = ci.Arg<Guid>(), TenantId = Tenant, Name = "board" });
        builder.Services.AddSingleton(boards);
        builder.Services.AddSingleton(Substitute.For<IWorkspaceConnector>());
        builder.Services.AddSingleton(Substitute.For<IAppConfigStore>());
        builder.Services.AddSingleton(Substitute.For<ISourceProviderResolver>());
        builder.Services.AddSingleton<IWorkspaceHostPolicy, AllowAllHosts>();
        builder.Services.AddSingleton<IWorkspaceAccess>(new WorkspaceAccess(_memberships));
        // Sessions
        builder.Services.AddSingleton(_sessions);
        builder.Services.AddSingleton(Substitute.For<IRunnerRepository>());
        builder.Services.AddSingleton<IRunnerProvisioningService, NoProvisioning>();
        builder.Services.AddSingleton(TimeProvider.System);
        // Settings
        builder.Services.AddSingleton(Substitute.For<ISettingsCatalog>());

        _app = builder.Build();
        // A stub answering with defaults may throw behind an ALLOWED call — that is a 500, not an authorization result.
        _app.Use(async (ctx, next) =>
        {
            try { await next(ctx); }
            catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or ArgumentException)
            {
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        });
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapTenantEndpoints();
        _app.MapWorkspaceEndpoints();
        _app.MapSessionEndpoints();
        _app.MapSettingsEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        // The restricted board has one member: u-dev as a developer.
        await _memberships.SetAsync(Tenant, RestrictedBoard, "u-dev", "developer", "admin");
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) { await _app.DisposeAsync(); }
    }

    private async Task<HttpStatusCode> SendAsync(string? roles, HttpMethod method, string path, object? body = null, string user = "u1")
    {
        using var request = new HttpRequestMessage(method, path);
        if (roles is not null)
        {
            request.Headers.Add(HeaderAuth.RolesHeader, roles);
            request.Headers.Add(HeaderAuth.UserHeader, user);
        }
        if (body is not null) { request.Content = JsonContent.Create(body); }
        using var response = await _client!.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    // (method, route, the permission the matrix requires)
    private static readonly (string Method, string Path, Permission Required)[] RouteTable =
    [
        ("GET", $"/tenants/{Tenant}/members", Permission.ManageMembers),
        ("PATCH", $"/tenants/{Tenant}/members/u2", Permission.ManageMembers),
        ("POST", $"/tenants/{Tenant}/invitations", Permission.ManageMembers),
        ("GET", $"/tenants/{Tenant}/audit", Permission.ViewAudit),
        ("GET", $"/tenants/{Tenant}/boards/{OpenBoard}/members", Permission.ManageMembers),
        ("PUT", $"/tenants/{Tenant}/boards/{OpenBoard}/members/u2", Permission.ManageMembers),
        ("GET", "/workspaces", Permission.ViewWork),
        ("GET", $"/workspaces/{OpenBoard}", Permission.ViewWork),
        ("POST", "/workspaces", Permission.ManageWorkspaces),
        ("DELETE", $"/workspaces/{OpenBoard}", Permission.ManageWorkspaces),
        ("POST", $"/workspaces/{OpenBoard}/repos", Permission.ManageWorkspaces),
        ("GET", "/sessions", Permission.ViewWork),
        ("POST", "/sessions", Permission.RunAgents),
        ("POST", "/runners", Permission.RunAgents),
        ("GET", "/settings/Llm", Permission.ManageSettings),
        ("POST", "/settings", Permission.ManageSettings),
    ];

    public static TheoryData<string, string, Permission> Routes()
    {
        var data = new TheoryData<string, string, Permission>();
        foreach (var (method, path, required) in RouteTable) { data.Add(method, path, required); }
        return data;
    }

    private static object? BodyFor(string method, string path) => (method, path) switch
    {
        ("PATCH", _) => new { roles = ViewerOnly },
        ("PUT", _) => new { role = "viewer" },
        ("POST", var p) when p.EndsWith("/invitations", StringComparison.Ordinal) => new { role = "viewer" },
        ("POST", "/sessions") => new { workspaceId = OpenBoard, title = "t" },
        ("POST", "/runners") => new { label = "laptop" },
        ("POST", "/settings") => new { key = "Llm:ForceProvider", value = "Claude" },
        ("POST", "/workspaces") => new { name = "b", projectOwner = "o", projectScope = "user", accessToken = "x" },
        ("POST", _) => new { owner = "o", repo = "r" },
        _ => null,
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_Anonymous_IsUnauthorized(string method, string path, Permission required)
    {
        _ = required;
        (await SendAsync(null, new HttpMethod(method), path, BodyFor(method, path))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    public static TheoryData<string, string, string, Permission> RoutesByRole()
    {
        var data = new TheoryData<string, string, string, Permission>();
        foreach (var role in UserRoles.Assignable.Append(UserRoles.LegacyMember).Append("platform-admin"))
        {
            foreach (var (method, path, required) in RouteTable)
            {
                data.Add(role, method, path, required);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RoutesByRole))]
    public async Task Route_EveryRole_GetsExactlyWhatTheMatrixGrants(string role, string method, string path, Permission required)
    {
        var status = await SendAsync(role, new HttpMethod(method), path, BodyFor(method, path));
        if (PermissionMatrix.For(role).Has(required))
        {
            status.ShouldNotBe(HttpStatusCode.Forbidden, $"{role} {method} {path}");
            status.ShouldNotBe(HttpStatusCode.Unauthorized, $"{role} {method} {path}");
        }
        else
        {
            status.ShouldBe(HttpStatusCode.Forbidden, $"{role} {method} {path}");
        }
    }

    // ── Per-board (workspace) membership isolation ──────────────────────────────────────────────

    [Fact]
    public async Task RestrictedBoard_IsNotFound_ForATenantDeveloperWhoIsNotAMember()
        => (await SendAsync("developer", HttpMethod.Get, $"/workspaces/{RestrictedBoard}", user: "u-other"))
            .ShouldBe(HttpStatusCode.NotFound);

    [Fact]
    public async Task RestrictedBoard_IsVisible_ToItsMember_AndToTenantWideRoles()
    {
        (await SendAsync("viewer", HttpMethod.Get, $"/workspaces/{RestrictedBoard}", user: "u-dev")).ShouldBe(HttpStatusCode.OK);
        (await SendAsync("auditor", HttpMethod.Get, $"/workspaces/{RestrictedBoard}", user: "u-other")).ShouldBe(HttpStatusCode.OK);
        (await SendAsync("admin", HttpMethod.Get, $"/workspaces/{RestrictedBoard}", user: "u-other")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BoardList_LeavesOutRestrictedBoards_TheCallerIsNotOn()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/workspaces");
        request.Headers.Add(HeaderAuth.RolesHeader, "developer");
        request.Headers.Add(HeaderAuth.UserHeader, "u-other");
        using var response = await _client!.SendAsync(request, TestContext.Current.CancellationToken);
        var rows = await response.Content.ReadFromJsonAsync<List<BoardRow>>(TestContext.Current.CancellationToken);
        rows!.Select(r => r.Id).ShouldBe([OpenBoard]);
    }

    [Fact]
    public async Task CreateSession_OnARestrictedBoard_FollowsTheMembershipRole()
    {
        _sessions.AddAsync(Arg.Any<RemoteSessionEntity>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var body = new { workspaceId = RestrictedBoard, title = "t" };
        // u-dev is the board's developer member: allowed.
        (await SendAsync("developer", HttpMethod.Post, "/sessions", body, user: "u-dev")).ShouldBe(HttpStatusCode.Created);
        // A tenant developer who is not on the restricted board may not start work there.
        (await SendAsync("developer", HttpMethod.Post, "/sessions", body, user: "u-other")).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BoardMembership_ForAnotherTenant_IsForbidden()
        => (await SendAsync("admin", HttpMethod.Get, $"/tenants/other/boards/{OpenBoard}/members")).ShouldBe(HttpStatusCode.Forbidden);

    [Fact]
    public async Task BoardMembership_RejectsARoleABoardCannotCarry()
        => (await SendAsync("admin", HttpMethod.Put, $"/tenants/{Tenant}/boards/{OpenBoard}/members/u2", new { role = "admin" }))
            .ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task MemberRoles_LockOutGuard_StillHolds()
        => (await SendAsync("admin", HttpMethod.Patch, $"/tenants/{Tenant}/members/u1", new { roles = ViewerOnly }, user: "u1"))
            .ShouldBe(HttpStatusCode.BadRequest);

    private sealed record BoardRow(Guid Id);

    private sealed class AllowAllHosts : IWorkspaceHostPolicy
    {
        public bool IsAllowed(string? host) => true;
    }

    private sealed class NoProvisioning : IRunnerProvisioningService
    {
        public Task<ProvisionedRunner> ProvisionAsync(string tenantId, string userId, string label, CancellationToken ct = default)
            => throw new InvalidOperationException("not under test");
    }

    /// <summary>Signs the caller in with the roles in <see cref="RolesHeader"/> (comma-separated) for tenant "acme".</summary>
    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "header";
        public const string RolesHeader = "X-Test-Roles";
        public const string UserHeader = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RolesHeader, out var roles)) { return Task.FromResult(AuthenticateResult.NoResult()); }
            var claims = new List<Claim>
            {
                new(ClaimNames.Subject, Request.Headers[UserHeader].ToString()),
                new(ClaimNames.Tenant, Tenant),
            };
            claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
