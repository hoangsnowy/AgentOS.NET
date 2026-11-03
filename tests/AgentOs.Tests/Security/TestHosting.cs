// In-memory hosts for pipeline-level integration tests (GAP-05 / GAP-17 / GAP-19): a real WebApplication on the
// ASP.NET Core TestServer, so the peer address, the Host header and cookies are under the test's control, plus a header-
// driven test identity. The identity handler doubles as the sign-out target of the "Cookies" / "OpenIdConnect" schemes
// so a test can see whether a request actually signed anyone out.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Tests.Security;

internal static class TestHosting
{
    /// <summary>Header naming the signed-in test user (absent = anonymous).</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>Comma-separated realm roles of the test user.</summary>
    public const string RolesHeader = "X-Test-Roles";

    /// <summary>The test user's tenant claim (absent = no claim).</summary>
    public const string TenantHeader = "X-Test-Tenant";

    public static WebApplicationBuilder Builder(string environment, IDictionary<string, string?>? configuration = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(TestHosting).Assembly.GetName().Name,
            ContentRootPath = System.AppContext.BaseDirectory,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration ?? new Dictionary<string, string?>());
        return builder;
    }

    /// <summary>Header-driven identity as the default scheme; "Cookies" and "OpenIdConnect" record sign-outs.</summary>
    public static SignOutRecorder AddTestAuth(this IServiceCollection services)
    {
        var recorder = new SignOutRecorder();
        services.AddSingleton(recorder);
        services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Cookies", _ => { })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("OpenIdConnect", _ => { });
        services.AddAuthorization(o =>
        {
            o.AddPolicy("Admin", p => p.RequireRole("admin"));
            o.AddPolicy("Member", p => p.RequireRole("admin", "member"));
            o.AddPolicy("PlatformAdmin", p => p.RequireRole("platform-admin"));
        });
        return recorder;
    }
}

internal sealed class SignOutRecorder
{
    public ConcurrentQueue<string> Schemes { get; } = new();
}

internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SignOutRecorder recorder)
    : SignInAuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TestHosting.UserHeader, out var user) || string.IsNullOrEmpty(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var claims = new List<Claim> { new(ClaimTypes.Name, user!), new("sub", user!) };
        claims.AddRange(Request.Headers[TestHosting.RolesHeader].ToString()
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .Select(r => new Claim(ClaimTypes.Role, r)));
        if (Request.Headers.TryGetValue(TestHosting.TenantHeader, out var tenant) && !string.IsNullOrEmpty(tenant))
        {
            claims.Add(new Claim("tenant", tenant!));
        }
        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties) => Task.CompletedTask;

    protected override Task HandleSignOutAsync(AuthenticationProperties? properties)
    {
        recorder.Schemes.Enqueue(Scheme.Name);
        return Task.CompletedTask;
    }
}
