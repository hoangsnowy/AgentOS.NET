// Composition root for the API host. All wiring is owned by the modules — this file is just an
// assembly list + ASP.NET Core lifecycle: AddServiceDefaults, AddModulesFromAssemblies, then
// MapModuleEndpoints + a couple of host-level health/meta routes.

using AgentOs.Api.Mcp;
using AgentOs.Domain.Llm;
using AgentOs.Modules.AppConfig;
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
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Fail-fast if a committed dev-default secret (Keycloak admin password / web client secret) is still in
// effect outside Development — those values are public (in git) and must never authenticate a real deploy.
AgentOs.SharedKernel.Security.DevSecretGuard.EnsureNoDevDefaults(builder.Configuration, builder.Environment.EnvironmentName);

// Console format + per-request tenant/traceId scopes come from ServiceDefaults (AddAgentOsLogging):
// JSON console in Production, human-readable elsewhere.

builder.Services.AddOpenApi();
// RFC 7807 error contract: unhandled endpoint exceptions become application/problem+json (status,
// title, traceId) instead of a bare 500 — no stack traces leave the process outside Development.
builder.Services.AddProblemDetails();
// Durable, shared key ring (Postgres-backed when configured) — survives restart/scale + decrypts across
// the Api ↔ Web hosts. Replaces a bare AddDataProtection() (in-memory, per-host).
builder.AddAgentOsDataProtection();

// S2 — per-tenant request rate limiting. An in-flight throttle on the request pipeline, INDEPENDENT
// of the month-to-date BudgetGuard (which is post-hoc and cannot bound a burst). Partitioned by the
// tenant claim so one tenant's spike can't starve another; a token bucket smooths bursts. Health +
// service-identity routes are exempt so liveness/readiness probes are never throttled.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var path = httpContext.Request.Path;
        if (path.StartsWithSegments("/health") || path.StartsWithSegments("/alive")
            || path.StartsWithSegments("/version") || path.Value == "/")
        {
            return RateLimitPartition.GetNoLimiter("exempt");
        }

        var tenant = httpContext.User.GetTenantId()
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        return RateLimitPartition.GetTokenBucketLimiter(tenant, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 120,
            TokensPerPeriod = 120,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

// Response compression — Brotli + Gzip for REST/JSON + Scalar responses. The default compressible
// MIME set excludes text/event-stream, so the streaming MCP endpoint (/mcp) is left uncompressed
// and un-buffered. Fastest level keeps CPU overhead low.
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
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

// Auth scheme is host-specific: API hosts Keycloak JWT bearer (Web does cookie + OIDC).
builder.Services.AddJwtAuth(builder.Configuration);

// Module discovery: each .Assembly contributes one IModule.
builder.Services.AddModulesFromAssemblies(builder.Configuration,
    typeof(AppConfigModule).Assembly,
    typeof(LlmModule).Assembly,
    typeof(IdentityModule).Assembly,
    typeof(TenantsModule).Assembly,
    typeof(PipelineModule).Assembly,
    typeof(ToolsModule).Assembly,
    typeof(IntegrationModule).Assembly,
    typeof(WorkspacesModule).Assembly,
    typeof(SessionsModule).Assembly,
    typeof(McpModule).Assembly,
    typeof(RemoteAgentModule).Assembly);

// Runtime plugins: discover IAgentOsPlugin assemblies dropped in the plugins folder (Plugins:Path,
// default "plugins" under the content root). A missing folder is a no-op.
var pluginsPath = builder.Configuration["Plugins:Path"] ?? "plugins";
builder.Services.AddPlugins(builder.Configuration,
    System.IO.Path.IsPathRooted(pluginsPath)
        ? pluginsPath
        : System.IO.Path.Join(builder.Environment.ContentRootPath, pluginsPath));

// Epic E4 — expose AgentOs pipeline as MCP server. WithToolsFromAssembly discovers every
// [McpServerToolType]-attributed class in the Api assembly (PipelineMcpTools today, more later).
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(PipelineMcpTools).Assembly);

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

// FIRST middleware: honour X-Forwarded-Proto/For from the Container Apps ingress so auth + link
// generation see the original https scheme + client IP.
app.UseAgentOsForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    // Renders unhandled exceptions through IProblemDetailsService (AddProblemDetails above).
    // Development keeps the developer exception page.
    app.UseExceptionHandler();
}

app.UseResponseCompression();

app.UseAuthentication();
app.UseAuthorization();

// After auth so the per-tenant partition can read the tenant claim (S2).
app.UseRateLimiter();

// Tenant + traceId on every log line written during the request (after auth: needs the claim).
app.UseAgentOsRequestLogContext();

if (!app.Environment.IsProduction())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("AgentOS API")
               .WithTheme(ScalarTheme.BluePlanet)
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });
}

// Build-stamped version (Directory.Build.props <Version>), surfaced on / and /version so operators
// can verify which release is running without inspecting container tags.
var informationalVersion = System.Reflection.CustomAttributeExtensions
    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Program).Assembly)
    ?.InformationalVersion ?? "0.0.0";

app.MapGet("/", () => Results.Ok(new
{
    name = "agentos",
    version = informationalVersion,
    status = "pipeline-ready"
}))
   .WithName("Root")
   .WithSummary("Service identity")
   .WithTags("Meta");

app.MapGet("/version", () => Results.Ok(new { name = "agentos-api", version = informationalVersion }))
   .WithName("Version")
   .WithSummary("Running release version (monitoring/upgrade verification)")
   .WithTags("Meta");

// /health (readiness: self + postgres + keycloak when configured) and /alive (liveness) come from
// ServiceDefaults — real dependency probes with a JSON body naming each failing check.
app.MapDefaultEndpoints();

// LLM provider configuration detail — kept off the readiness path: a missing LLM key is a
// configuration state the Settings UI surfaces, not a reason for the orchestrator to recycle pods.
app.MapGet("/health/llm", (IOptions<LlmOptions> llm) =>
{
    var o = llm.Value;
    var claudeReady = !string.IsNullOrWhiteSpace(o.Claude.ApiKey);
    var azureReady = !string.IsNullOrWhiteSpace(o.AzureOpenAi.ApiKey) && !string.IsNullOrWhiteSpace(o.AzureOpenAi.Endpoint);
    return Results.Ok(new
    {
        status = "Healthy",
        utc = DateTime.UtcNow,
        llm = new
        {
            provider = o.Provider,
            forceProvider = o.ForceProvider,
            claudeKeyConfigured = claudeReady,
            azureKeyConfigured = azureReady,
        },
    });
})
   .WithName("HealthLlm")
   .WithSummary("LLM provider configuration readiness (not a pod-recycle signal)")
   .WithTags("Meta");

app.MapModuleEndpoints();

// Epic E4 — MCP HTTP endpoint at /mcp. Streamable HTTP per spec. RequireAuthorization so an
// anonymous caller can't drive the full 5-agent LLM pipeline (uncapped spend) or read another
// tenant's runs — every sibling REST route is gated, this one must be too.
app.MapMcp("/mcp").RequireAuthorization();

// Settings "Test connection" — probe the configured provider with a minimal call.
app.MapPost("/llm/test", async (ILlmClientFactory factory, IConfiguration cfg, CancellationToken ct) =>
{
    var provider = cfg["Agents:Orchestrator:Provider"] ?? "Anthropic";
    var model = cfg["Agents:Orchestrator:Model"] ?? "claude-haiku-4-5";
    try
    {
        var client = factory.Create(provider);
        var probe = new LlmRequest("You are a connectivity probe.", "Reply with the single word: OK", model, 0.0, 5);
        var resp = await client.SendAsync(probe, ct).ConfigureAwait(false);
        // Report the provider that ANSWERED (client.Provider is the chain's primary, which is wrong under
        // failover), and refuse to call it ok when the canned Offline safety net stood in for the provider
        // being probed — a probe that greenlights unreachable-but-faked is worse than one that fails.
        if (AgentOs.Modules.Llm.OfflineLlmClient.IsSubstituteFor(provider, resp.Provider))
        {
            return Results.Ok(new
            {
                ok = false,
                provider,
                model,
                error = $"{provider} did not answer — canned {resp.Provider} output was served instead "
                    + "(Llm:OfflineFallback is on). Nothing reached a real model.",
            });
        }
        return Results.Ok(new { ok = true, provider = resp.Provider, model, sample = resp.Content?.Trim() });
    }
    catch (System.Net.Http.HttpRequestException ex)
    {
        return Results.Ok(new { ok = false, provider, model, error = ex.Message });
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.Ok(new { ok = false, provider, model, error = ex.Message });
    }
    catch (TimeoutException ex)
    {
        return Results.Ok(new { ok = false, provider, model, error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Ok(new { ok = false, provider, model, error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.Ok(new { ok = false, provider, model, error = ex.Message });
    }
    catch (LlmException ex)
    {
        // The gateway's own type for "no key configured" / "all keys exhausted" — the exact state this probe
        // exists to diagnose. Without this it would escape as a 500 and the Web's split-mode probe would only
        // see a JsonException fallback instead of the friendly error.
        return Results.Ok(new { ok = false, provider, model, error = ex.Message });
    }
#pragma warning disable CA1031 // Probe endpoint: surface ANY provider/SDK failure (e.g. Azure RequestFailedException) as {ok:false}, never a 500.
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, provider, model, error = ex.Message });
    }
#pragma warning restore CA1031
})
   .WithName("LlmTest")
   .WithSummary("Probe the configured LLM provider with a minimal call")
   .WithTags("Settings")
   .RequireAuthorization();

app.Run();
