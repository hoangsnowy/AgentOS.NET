// Composition root for the Blazor Server host. Loads the same modules as the API; auth is
// host-specific — Web wires Cookie + OpenID Connect against Keycloak realm "agentic", whereas the API
// uses JWT bearer. /account/login & /account/logout drive the challenge / sign-out round-trips; the
// OIDC middleware handles /signin-oidc & callback.

using System.Security.Claims;
using AgentOs.Modules.AppConfig;
using AgentOs.Web.Auth;
using AgentOs.Modules.Identity;
using AgentOs.Modules.Identity.Auth;
using AgentOs.Modules.Integration;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Mcp;
using AgentOs.Modules.Pipeline;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Sessions;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tools;
using AgentOs.Modules.Workspaces;
using AgentOs.ServiceDefaults;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Modularity;
using AgentOs.SharedKernel.Plugins;
using AgentOs.Web.Components;
using AgentOs.Web.Services;
using AgentOs.Web.Shell.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Console format + per-request tenant/traceId scopes come from ServiceDefaults (AddAgentOsLogging):
// JSON console in Production, human-readable elsewhere.

// App Insights when a connection string is provided (parity with the Api host).
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Durable, shared key ring (Postgres-backed when configured) — without it the OIDC correlation/nonce
// cookies + the auth cookie + encrypted tenant secrets break on every restart/scale and don't decrypt
// across the Api ↔ Web hosts. Replaces a bare AddDataProtection() (in-memory, per-host).
builder.AddAgentOsDataProtection();

// Response compression — Brotli + Gzip for dynamic responses (the initial Razor document, /health,
// any non-static endpoint). NOTE: static assets are ALREADY compressed at build time by
// MapStaticAssets, and Blazor Server's per-interaction traffic rides a WebSocket (which this
// middleware does not touch), so this trims first-load transfer — it is not a fix for interaction
// latency (that is build config + SignalR round-trips). Fastest level keeps CPU cost low.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

// Dev single-command run: auto-authenticate as a fixed developer principal so `dotnet run --project
// src/AgentOs.Web` shows the desktop with no Keycloak / Postgres. Defaults ON in Development (so a fresh
// clone just works — appsettings.Development.json is gitignored and can't be relied on); the AppHost
// injects Auth__DevAutoLogin=false so the full stack uses real OIDC, and it is hard-off outside Development.
var devAutoLogin = builder.Configuration.GetValue("Auth:DevAutoLogin", builder.Environment.IsDevelopment());
if (devAutoLogin && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Auth:DevAutoLogin must never be enabled outside Development — it authenticates every request as a fixed user.");
}

// Fail-fast if a committed dev-default secret (admin password, web client secret) is still in effect
// outside Development — those values are public and must never authenticate a real deployment.
AgentOs.SharedKernel.Security.DevSecretGuard.EnsureNoDevDefaults(builder.Configuration, builder.Environment.EnvironmentName);

// Surface dev-mode to the UI so dev-only affordances (the "View as" role-preview menu) render only
// when auto-login is active, never under real Keycloak auth.
builder.Services.AddSingleton(new AgentOs.Web.Services.DevModeState(devAutoLogin));

if (devAutoLogin)
{
    builder.Services
        .AddAuthentication(DevAutoAuthHandler.SchemeName)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevAutoAuthHandler>(
            DevAutoAuthHandler.SchemeName, _ => { });
}
else
{

// Cookie + OpenID Connect against Keycloak. The cookie carries the signed-in principal across
// HTTP requests; SaveTokens=true stores the access token in the auth cookie so circuit-scoped
// AuthSession can forward it to outbound API calls when needed.
var keycloak = builder.Configuration.GetSection("Auth:Keycloak");
var keycloakClientSecret = keycloak["ClientSecret"];
if (string.IsNullOrWhiteSpace(keycloakClientSecret) && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Auth:Keycloak:ClientSecret is required outside the Development environment. " +
        "Set it via Aspire parameters, user-secrets, or an environment variable.");
}
// Default true: code that forgets to override picks the secure setting. Dev overrides via
// appsettings.Development.json or the AppHost env injection.
var requireHttps = !bool.TryParse(keycloak["RequireHttpsMetadata"], out var rh) || rh;
// Dev fallback only — resolved HERE (startup) so a production Web with real OIDC and no Authority
// fails at boot, not on the first sign-in redirect to a localhost that doesn't exist.
var oidcAuthority = keycloak["Authority"] is { Length: > 0 } configuredAuthority
    ? configuredAuthority
    : builder.Environment.IsDevelopment()
        ? "http://localhost:8080/realms/agentic"
        : throw new InvalidOperationException(
            "Auth:Keycloak:Authority is required outside Development when DevAutoLogin is off.");
// Per-instance cookie names. Cookies are scoped to the HOST, not the port, so two AgentOS instances on the
// same host (dev + lab on localhost, or several deployments under one parent domain) would otherwise share
// and overwrite each other's auth/correlation/antiforgery cookies. The default prefix keeps today's names.
var cookiePrefix = AuthCookies.Prefix(builder.Configuration["Auth:CookiePrefix"]);
builder.Services.AddAntiforgery(o => o.Cookie.Name = $"{cookiePrefix}.af");

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = $"{cookiePrefix}.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Always send the cookie over HTTPS in non-Development; in dev we may run plain http://
        // (Aspire pins port 5180), so flex with the request scheme.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = oidcAuthority;
        // Behind ACA the public authority is not hairpin-reachable from inside the environment.
        // When set, MetadataAddress points the back-channel (discovery/token/jwks) at the internal
        // http endpoint while Authority stays the public https URL for browser redirects + issuer.
        if (keycloak["MetadataAddress"] is { Length: > 0 } oidcMetadata)
        {
            options.MetadataAddress = oidcMetadata;
        }
        options.ClientId = keycloak["ClientId"] ?? "agentic-web";
        options.ClientSecret = keycloakClientSecret ?? string.Empty;
        options.ResponseType = "code";
        options.UsePkce = true;
        // Disable Pushed Authorization Requests. .NET's OIDC handler auto-enables PAR whenever the IdP
        // advertises a pushed_authorization_request_endpoint (Keycloak does), so the browser is redirected
        // to /auth?request_uri=urn:… and Keycloak must resolve that reference from its in-memory PAR store.
        // Restarting Keycloak (or a stale browser session cookie) drops that reference → Keycloak can't
        // recover the pushed params → "Missing parameter: response_type" and login dead-ends. The realm
        // does not require PAR, so the classic code flow (response_type carried in the URL) is both valid
        // and robust — no server-side reference to lose, so a Keycloak restart or stale cookie can't break it.
        options.PushedAuthorizationBehavior = Microsoft.AspNetCore.Authentication.OpenIdConnect.PushedAuthorizationBehavior.Disable;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.RequireHttpsMetadata = requireHttps;
        // Correlation + nonce cookies default to SameSite=None, which the browser only stores when
        // also Secure. Behind Aspire's dcp TLS-terminating proxy the app can see the request as http,
        // so the cookie is written WITHOUT Secure → Chrome drops it → "Correlation failed" at
        // /signin-oidc. The code flow's callback is a top-level GET, so SameSite=Lax is sufficient and
        // is sent without requiring Secure; SameAsRequest keeps it Secure when the scheme really is https.
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.CorrelationCookie.Name = $"{cookiePrefix}.oidc.correlation.";
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.NonceCookie.Name = $"{cookiePrefix}.oidc.nonce.";
        // Realm `agentic-web` client attaches `tenant` + `realm-roles` + `preferred-username` +
        // `email` claims via inline protocol mappers — we only request the `openid` base scope.
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "preferred_username",
            RoleClaimType = ClaimTypes.Role,
        };
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = ctx => { JwtAuthExtensions.FlattenRealmRoles(ctx.Principal); return Task.CompletedTask; },
            OnTicketReceived = ctx => { AuthCookies.ClearRetry(ctx.HttpContext, cookiePrefix); return Task.CompletedTask; },
            // A callback that can't be completed (correlation/nonce cookie lost or expired, a stale tab, the
            // IdP restarted) restarts the sign-in ONCE instead of throwing an exception page; a second
            // failure lands on a plain "couldn't sign you in" page with the reason and a retry link.
            OnRemoteFailure = ctx => AuthCookies.HandleRemoteFailureAsync(ctx, cookiePrefix),
        };
    });

} // end else (real Keycloak OIDC)

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddModulesFromAssemblies(builder.Configuration,
    typeof(AppConfigModule).Assembly,
    typeof(LlmModule).Assembly,
    typeof(IdentityModule).Assembly,
    typeof(TenantsModule).Assembly,
    typeof(PipelineModule).Assembly,
    typeof(IntegrationModule).Assembly,
    typeof(WorkspacesModule).Assembly,
    typeof(SessionsModule).Assembly,
    typeof(ToolsModule).Assembly,
    // MCP client: the in-process pipeline gets upstream MCP tools too (previously Api-only),
    // and the MCP admin app can read the connection statuses.
    typeof(McpModule).Assembly,
    // RemoteAgent MUST be loaded here, not just on the Api. Board's "Run on my machine" toggle sets
    // IssueWorkRequest.ProviderOverride = "RemoteAgent", and the session it belongs to executes in
    // THIS process (BoardApp.RunSession → Task.Run → ScopeFactory.CreateScope). The keyed
    // ILlmClient under "RemoteAgent" is registered by RemoteAgentModule alone, so without this the
    // toggle threw "LLM provider 'RemoteAgent' … is not registered" — a shipped control the host
    // rendering it could not honour. Loading the module here also maps /hubs/remote-agent on the Web
    // origin: the broker is in-process with no backplane, so the runner must pair to the same host
    // that dispatches to it.
    typeof(RemoteAgentModule).Assembly);

// Runtime plugins: discover IAgentOsPlugin assemblies dropped in the plugins folder (Plugins:Path,
// default "plugins" under the content root). A missing folder is a no-op.
var pluginsPath = builder.Configuration["Plugins:Path"] ?? "plugins";
builder.Services.AddPlugins(builder.Configuration,
    System.IO.Path.IsPathRooted(pluginsPath)
        ? pluginsPath
        : System.IO.Path.Join(builder.Environment.ContentRootPath, pluginsPath));

builder.Services.AddSingleton<AgentOs.Web.Orchestrations.OrchestrationStore>();
// Per-circuit UI state: each user's desktop has its own open windows. Singleton would bleed windows
// (and their Z-order) across every connected circuit/user on the server.
builder.Services.AddScoped<AgentOs.Web.Shell.Services.ToastService>();
builder.Services.AddScoped<AgentOs.Web.Shell.Services.WindowManagerService>();
// Shell seam: the RCL's WindowHost resolves an app's component through this; the host implements it from
// its AppCatalog. Singleton — the app catalog is global (same for every circuit), filtered per call.
builder.Services.AddSingleton<AgentOs.Web.Shell.Services.IAppRegistry, AgentOs.Web.Services.AppCatalogRegistry>();
builder.Services.AddScoped<AgentOs.Web.Orchestrations.GraphRunnerService>();
builder.Services.AddScoped<AgentOs.Web.Services.WorkspacePrTargetService>();
builder.Services.AddScoped<AgentOs.Web.Services.LlmConfigView>();

// Per-circuit auth session — surfaces identity + (optional) bearer to the HttpPipelineClient.
builder.Services.AddScoped<AuthSession>();
builder.Services.AddScoped<IAuthTokenProvider>(sp => sp.GetRequiredService<AuthSession>());

builder.Services.AddHttpClient();

// System → About: live status of every service (Web, Api, Keycloak, Postgres, SMTP). Probes are singletons
// behind a cached, single-flight aggregator; the probe HttpClient has NO retry pipeline so a dead service
// shows as Down within the probe budget instead of hiding behind retries.
builder.Services.AddSingleton<AgentOs.Web.Services.About.ProbeHttpClient>();
builder.Services.AddSingleton<AgentOs.Web.Services.About.CircuitCounter>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, AgentOs.Web.Services.About.CircuitCounterHandler>();
// Every circuit event runs under the signed-in tenant (AmbientIdentity) — feeds row-level security (ADR-0012).
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, AgentOs.Web.Services.CircuitIdentityHandler>();
builder.Services.AddSingleton<AgentOs.Web.Services.About.IServiceProbe, AgentOs.Web.Services.About.WebHostProbe>();
builder.Services.AddSingleton<AgentOs.Web.Services.About.IServiceProbe, AgentOs.Web.Services.About.ApiProbe>();
builder.Services.AddSingleton<AgentOs.Web.Services.About.IServiceProbe, AgentOs.Web.Services.About.KeycloakProbe>();
builder.Services.AddSingleton<AgentOs.Web.Services.About.IServiceProbe, AgentOs.Web.Services.About.PostgresProbe>();
builder.Services.AddSingleton<AgentOs.Web.Services.About.IServiceProbe, AgentOs.Web.Services.About.SmtpProbe>();
builder.Services.AddSingleton<AgentOs.Web.Services.About.SystemStatusService>();

var app = builder.Build();

await app.Services.InitializeModulesAsync();

// E0.1 / ADR-0012: Postgres row-level security (stage 1), after every module migrated. Each tenant-table
// connection then switches to the restricted role with the caller's tenant — an authenticated request's
// claim, or the ambient identity a run / desktop circuit pushed. Escape hatch: Persistence:RowLevelSecurity=false.
{
    var httpAccessor = app.Services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
    AgentOs.SharedKernel.Persistence.RowLevelSecurity.HttpTenant = () =>
        httpAccessor.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user.GetTenantId() : null;
    if (app.Configuration.GetValue("Persistence:RowLevelSecurity", true))
    {
        await AgentOs.SharedKernel.Persistence.RowLevelSecurity.EnsureAsync(app.Configuration.GetConnectionString("DefaultConnection"), app.Logger);
    }
}

// Surface plugin-contributed desktop windows: collect every PluginAppDescriptor a plugin registered and
// add it to the shell catalog (the WindowHost renders these via DynamicComponent).
AppCatalog.RegisterPluginApps(app.Services.GetServices<PluginAppDescriptor>().Select(d =>
    new DesktopApp(d.Key, d.Title, d.Icon, d.Caption, "Plugins", d.Width, d.Height, Pinned: true,
        AdminOnly: d.AdminOnly, ComponentType: d.ComponentType)));

// FIRST middleware: honour X-Forwarded-Proto/For from the Container Apps ingress. Critical for the Web
// — the OIDC redirect_uri + secure-cookie decisions depend on the request being seen as https.
app.UseAgentOsForwardedHeaders();

// Early in the pipeline so it wraps every downstream response. Skips assets MapStaticAssets already
// served pre-compressed (their Content-Encoding is set), so there is no double-compression.
app.UseResponseCompression();

if (!app.Environment.IsDevelopment())
{
    // Branded static crash screen (no routing/auth/circuit — see ErrorPage for why not a component).
    app.UseExceptionHandler(errorApp => errorApp.Run(AgentOs.Web.ErrorPage.HandleAsync));
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Tenant + traceId on every log line written during the request (after auth: needs the claim).
app.UseAgentOsRequestLogContext();

// /health (readiness: self + postgres + keycloak when configured) and /alive (liveness) come from
// ServiceDefaults — real dependency probes with a JSON body naming each failing check.
app.MapDefaultEndpoints();

// Build-stamped version (Directory.Build.props <Version>) for operators/monitoring.
var informationalVersion = System.Reflection.CustomAttributeExtensions
    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Program).Assembly)
    ?.InformationalVersion ?? "0.0.0";
app.MapGet("/version", () => Results.Ok(new { name = "agentos-web", version = informationalVersion }));

// Serve the self-contained AgentOs.RemoteAgent exe so the VS Code extension (and the Runners tab) can
// fetch the runner with no .NET SDK and no source checkout. scripts/build-runner.ps1 publishes one
// single-file binary per RID into runner-dist/<rid>/; the extension sends ?rid= for its OS. win-x64 is
// the default and also falls back to the legacy flat path. Anonymous — the runner is the public
// open-source binary and carries no secrets (pairing happens via a separate token).
//
// Resolve the published binary path for a RID, or null if that platform isn't built / RID is unknown.
static string? ResolveRunnerPath(string contentRoot, string rid)
{
    string[] known = ["win-x64", "linux-x64", "osx-x64", "osx-arm64"];
    if (!known.Contains(rid))
    {
        return null;
    }
    var file = rid.StartsWith("win", StringComparison.Ordinal) ? "AgentOs.RemoteAgent.exe" : "AgentOs.RemoteAgent";
    var path = System.IO.Path.Join(contentRoot, "runner-dist", rid, file);
    if (System.IO.File.Exists(path))
    {
        return path;
    }
    // Legacy flat layout (pre multi-RID builds) — Windows only.
    if (rid == "win-x64")
    {
        var legacy = System.IO.Path.Join(contentRoot, "runner-dist", "AgentOs.RemoteAgent.exe");
        if (System.IO.File.Exists(legacy))
        {
            return legacy;
        }
    }
    return null;
}

app.MapGet("/runner/download", (IHostEnvironment env, string? rid) =>
{
    var resolved = ResolveRunnerPath(env.ContentRootPath, string.IsNullOrWhiteSpace(rid) ? "win-x64" : rid);
    if (resolved is null)
    {
        return Results.NotFound(
            $"No runner binary for '{rid ?? "win-x64"}'. Run scripts/build-runner.ps1 on the server to publish it.");
    }
    var downloadName = resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "agentos-runner.exe" : "agentos-runner";
    return Results.File(resolved, "application/octet-stream", downloadName);
});

// Version probe for the extension's auto-update: a cheap build token (size + mtime) of the published
// binary for a RID. The extension stores it and re-downloads when it changes. No assembly-version
// plumbing needed; the token only has to differ when the bytes do.
app.MapGet("/runner/version", (IHostEnvironment env, string? rid) =>
{
    var resolved = ResolveRunnerPath(env.ContentRootPath, string.IsNullOrWhiteSpace(rid) ? "win-x64" : rid);
    if (resolved is null)
    {
        return Results.NotFound($"No runner binary for '{rid ?? "win-x64"}'.");
    }
    var info = new System.IO.FileInfo(resolved);
    var token = string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}");
    return Results.Text(token);
});

// Cost CSV export for the admin Cost app. Mapped here on the Web (not via the API's MapModuleEndpoints)
// so the Cost circuit can trigger a same-origin, cookie-authed browser download — like /runner/download.
// Tenant comes from the OIDC 'tenant' claim; admin-gated; `days` (0/absent = all time) sets the cutoff.
app.MapGet("/cost/export", async (HttpContext http, AgentOs.Modules.Pipeline.Persistence.IPipelineRunRepository runs, int? days) =>
{
    if (!http.User.IsAdmin())
    {
        return Results.Forbid();
    }

    var tenant = http.User.GetTenantId() ?? "default";
    DateTimeOffset? since = days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : null;
    var summary = await runs.GetCostSummaryForTenantAsync(tenant, since);
    var bytes = System.Text.Encoding.UTF8.GetBytes(AgentOs.Modules.Pipeline.Cost.CostCsv.ToCsv(summary));
    return Results.File(bytes, "text/csv", "agentos-cost.csv");
}).RequireAuthorization();

// Audit CSV export (Settings → Audit log). Same-origin cookie-authed download like /cost/export. A tenant admin
// exports their own tenant; scope=platform (platform settings changes) needs platform-admin.
app.MapGet("/audit/export", async (HttpContext http, AgentOs.Modules.Tenants.IAuditLog audit, string? scope) =>
{
    var platform = string.Equals(scope, "platform", StringComparison.Ordinal);
    if (!http.User.IsAdmin() || (platform && !http.User.IsPlatformAdmin()))
    {
        return Results.Forbid();
    }
    var tenant = platform ? AgentOs.Modules.Tenants.AuditScopes.Platform : http.User.GetTenantId() ?? "default";
    var rows = await audit.ListAsync(tenant, 10_000);
    var bytes = System.Text.Encoding.UTF8.GetBytes(AgentOs.Modules.Tenants.AuditCsv.ToCsv(rows));
    return Results.File(bytes, "text/csv", platform ? "agentos-audit-platform.csv" : $"agentos-audit-{tenant}.csv");
}).RequireAuthorization();

// VS Code browser-pairing: the approve page (/pair/vscode, OIDC-cookie authed) + the one-time code
// exchange (/runner/pair/exchange). Mapped here on the Web — the extension and browser both target the
// Web origin, and the approve step uses the browser session, not a Bearer token.
AgentOs.Modules.Sessions.Endpoints.PairingEndpoints.MapPairingEndpoints(app);

// The runner's SignalR hub, mapped explicitly rather than via MapModuleEndpoints — this host serves a
// UI, not the API's REST surface, so it opts in one endpoint at a time (same reason PairingEndpoints
// is mapped by hand above). The hub must exist on THIS origin because IRemoteAgentBroker is in-process
// with no backplane: Board's "Run on my machine" dispatch runs in the Web, so a runner paired to the
// Api's hub would be invisible to it. Anonymous by design — the hub authenticates the connection
// itself against the runner's salted pairing-token hash (see RemoteAgentHub.OnConnectedAsync), so
// there is no browser session to require here.
app.MapHub<AgentOs.Modules.RemoteAgent.RemoteAgentHub>(AgentOs.Modules.RemoteAgent.RemoteAgentHub.Path);

// OIDC challenge / sign-out — buttons in the UI hit these endpoints. /signin-oidc and
// /signout-callback-oidc are owned by the OIDC middleware. In dev-auto-login mode there are no
// Cookie/OIDC schemes, so these become simple redirects (the dev user is always signed in).
// Only follow a returnUrl that is a local path (starts with a single '/'), never an absolute or
// protocol-relative URL — otherwise /account/login?returnUrl=https://evil is an open redirect.
static string SafeLocalReturn(string? url) =>
    !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";

if (devAutoLogin)
{
    app.MapGet("/account/login", (string? returnUrl) =>
        Results.Redirect(SafeLocalReturn(returnUrl)));
    app.MapGet("/account/logout", () => Results.Redirect("/"));

    // Dev-only "View as": narrow the auto-login principal's roles to preview the member (or admin)
    // desktop without standing up a second Keycloak user. Writes the cookie DevAutoAuthHandler reads,
    // then reloads so the new principal takes effect. Available only because devAutoLogin is on.
    app.MapGet("/dev/view-as", (HttpContext ctx, string? role) =>
    {
        if (role is "admin" or "member")
        {
            ctx.Response.Cookies.Append(DevAutoAuthHandler.ViewAsCookie, role,
                new Microsoft.AspNetCore.Http.CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                    Path = "/",
                });
        }
        else
        {
            ctx.Response.Cookies.Delete(DevAutoAuthHandler.ViewAsCookie);
        }
        return Results.Redirect("/");
    });
}
else
{
    app.MapGet("/account/login", (string? returnUrl) =>
        Results.Challenge(
            new AuthenticationProperties { RedirectUri = SafeLocalReturn(returnUrl) },
            new[] { OpenIdConnectDefaults.AuthenticationScheme }));

    // Shown only after a sign-in callback failed twice in a row (see AuthCookies.HandleRemoteFailureAsync).
    app.MapGet("/account/signin-failed", (string? reason, string? returnUrl) => AuthCookies.FailedPage(reason, returnUrl))
        .AllowAnonymous();

    app.MapGet("/account/logout", () =>
        Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            new[] { CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme }));
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.Run();
