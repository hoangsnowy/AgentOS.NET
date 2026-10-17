// Aspire AppHost — single F5 brings up Postgres + Keycloak + MailHog + API + Web for local dev.
// azd promotes Postgres to Azure Database for PostgreSQL flexible server in the cloud. The DB
// resource is named "DefaultConnection" so WithReference injects ConnectionStrings__DefaultConnection.
// Keycloak realm "agentic" is auto-imported from ./infra/keycloak; its HTTP URL is forwarded as
// Auth__Keycloak__Authority so the API (JWT bearer) and the Web (OIDC code flow) both wire against
// it without any hardcoded URL. Web pins the HTTP endpoint to 5180 to match the realm's
// `agentic-web` client redirectUris. MailHog catches all dev verification emails on UI port 8025;
// Keycloak sends to it via the realm-level smtpServer config (host=mailhog port=1025).
//
// --- Cloud bootstrap (single pass + realm patch) ---
// Keycloak is durable from the FIRST `azd up`: the publish branch provisions a 'keycloak' database
// on the same Azure Postgres flexible server (password auth — Keycloak's JDBC driver and the app's
// plain Npgsql can't do Entra tokens) and wires KC_DB_* from the server's bicep outputs. No manual
// JDBC step. KC auto-detects its public hostname from the ACA forwarded headers
// (KC_HOSTNAME_STRICT=false + KC_PROXY_HEADERS=xforwarded + KC_HTTP_ENABLED=true — Keycloak 24+
// removed the old KC_PROXY=edge option). Before the first `azd up`, set the required secrets:
//         azd env set KEYCLOAKADMINPASSWORD    "<strong-password>"
//         azd env set KEYCLOAKWEBCLIENTSECRET  "<strong-secret>"
//         azd env set POSTGRESPASSWORD         "<strong-password>"
// After the first deploy, set the public URLs and re-run the realm patch hook (azd runs it on every
// provision; it skips silently until these are set):
//         azd env set KEYCLOAK_BASE_URL "https://keycloak.<env>.<region>.azurecontainerapps.io"
//         azd env set WEB_BASE_URL      "https://web.<env>.<region>.azurecontainerapps.io"
// The hook (infra/hooks/postdeploy.*) patches realm redirectUris/webOrigins + the agentic-web
// client secret to match KEYCLOAKWEBCLIENTSECRET, rotates seed-user passwords, and disables
// verifyEmail in cloud (no SMTP yet — roadmap D4).
//
// Secrets (Keycloak admin password + agentic-web client secret + Postgres password) are Aspire
// Parameters; their dev defaults live in appsettings.json under "Parameters". Override
// per-environment via `dotnet user-secrets`, `azd env set`, or environment variables — never edit
// the dev defaults.
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

// Run mode = local `dotnet run` (single-F5 dev stack). Publish mode = `azd`/manifest generation for the
// cloud. Dev-only affordances (theme bind-mount, MailHog, Web Development pin) are gated to run mode.
var isPublish = builder.ExecutionContext.IsPublishMode;

// Stack profile (run mode only). "dev" (default) is the everyday stack; "lab" is a long-running test
// server for real GitHub / Azure DevOps / LLM integrations that runs SIDE BY SIDE with dev — every host
// port and every data volume is distinct, so neither stack can clobber the other. Launch profile "lab"
// (`dotnet run --project infra/AgentOs.AppHost --launch-profile lab`) sets AgentOs__Stack=lab.
var stack = new StackProfile(builder.Configuration["AgentOs:Stack"]);

// --- Auth parameters ---
var kcAdminUsername   = builder.AddParameter("KeycloakAdminUsername");
var kcAdminPassword   = builder.AddParameter("KeycloakAdminPassword", secret: true);
var kcWebClientSecret = builder.AddParameter("KeycloakWebClientSecret", secret: true);

// --- Email (dev → MailHog localhost:1025; cloud → real SMTP via azd env set SMTPHOST / SMTPPORT) ---
var smtpHost = builder.AddParameter("SmtpHost");
var smtpPort = builder.AddParameter("SmtpPort");

// --- App database (local: Docker container; cloud: Azure Postgres Flexible Server) ---
// Password auth, not Entra: the app talks plain Npgsql (UseNpgsql with a connection string, no
// token plumbing) and Keycloak talks JDBC — neither can use the AzurePostgresqlAuthenticationPlugin
// that Aspire's default Entra-only provisioning assumes.
var pgUsername = builder.AddParameter("PostgresUsername");
var pgPassword = builder.AddParameter("PostgresPassword", secret: true);
var postgres = builder.AddAzurePostgresFlexibleServer("postgres")
    .WithPasswordAuthentication(pgUsername, pgPassword)
    .RunAsContainer(c => c.WithDataVolume(stack.Volume("postgres-data")));
var db = postgres.AddDatabase("DefaultConnection", databaseName: "agentos");
// Keycloak's durable store — created by the same bicep so KC is Postgres-backed from the first
// `azd up` (no manual "create the keycloak database" step).
postgres.AddDatabase("keycloak-db", databaseName: "keycloak");

// --- MailHog: dev SMTP catcher only — never ship to cloud ---
IResourceBuilder<ContainerResource>? mailhog = null;
if (!isPublish)
{
    mailhog = builder.AddContainer("mailhog", "mailhog/mailhog")
        .WithHttpEndpoint(port: stack.Port(8025), targetPort: 8025, name: "ui")
        .WithEndpoint(port: stack.Port(1025), targetPort: 1025, name: "smtp", scheme: "tcp");
}

// --- Keycloak ---
var keycloak = builder.AddKeycloak("keycloak", port: stack.Port(8080))
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", kcAdminUsername)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", kcAdminPassword);

if (!isPublish)
{
    // Local dev: H2 volume (persists between runs) + realm import via bind-mount + theme hot-reload.
    // KC scans /opt/keycloak/themes/<name>/ for themes; bind-mount our source dir so CSS edits
    // hot-reload without rebuilding the container. Dev cookie-jar pollution on localhost can exceed
    // the Quarkus default max header size → HTTP 431; QUARKUS_HTTP_LIMITS_MAX_HEADER_SIZE fixes it.
    // 512K matches Kestrel's MaxRequestHeadersTotalSize (ServiceDefaults) — 128K wasn't enough on a
    // heavy dev box, so the whole OIDC round-trip survives without clearing cookies / incognito.
    keycloak
        .WithDataVolume(stack.Volume("keycloak-data"))
        .WithRealmImport("../../infra/keycloak")
        .WaitFor(mailhog!)
        .WithBindMount("../../infra/keycloak/themes/agentos", "/opt/keycloak/themes/agentos")
        .WithEnvironment("KC_SPI_THEME_STATIC_MAX_AGE", "-1")
        .WithEnvironment("KC_SPI_THEME_CACHE_THEMES", "false")
        .WithEnvironment("KC_SPI_THEME_CACHE_TEMPLATES", "false")
        .WithEnvironment("QUARKUS_HTTP_LIMITS_MAX_HEADER_SIZE", "512K");
}
else
{
    // Cloud: custom image (infra/keycloak/Dockerfile) bakes realm JSON + theme into the image so
    // bind-mounts aren't needed. ACA's ingress terminates TLS and forwards plain HTTP with
    // X-Forwarded-* headers, so KC must (a) accept HTTP (KC_HTTP_ENABLED — `start` mode refuses
    // plain HTTP otherwise), (b) trust the forwarded headers (KC_PROXY_HEADERS=xforwarded — the
    // old KC_PROXY=edge option was removed in Keycloak 24), and (c) derive its public URL from the
    // request (KC_HOSTNAME_STRICT=false) — the ACA FQDN is stable, so the issuer stays consistent.
    keycloak
        .WithDockerfile("../../infra/keycloak")
        .WithEnvironment("KC_HTTP_ENABLED", "true")
        .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
        .WithEnvironment("KC_HOSTNAME_STRICT", "false")
        // Browser must reach the login page + OIDC endpoints → external ingress (http 8080 only;
        // the management endpoint stays internal).
        .WithEndpoint("http", e => e.IsExternal = true)
        // Durable Postgres backend from the first deploy — same flexible server, 'keycloak' DB,
        // composed from the server's bicep outputs (no manual JDBC bootstrap step).
        .WithEnvironment("KC_DB", "postgres")
        .WithEnvironment("KC_DB_URL", KeycloakJdbcUrl(postgres))
        .WithEnvironment("KC_DB_USERNAME", pgUsername)
        .WithEnvironment("KC_DB_PASSWORD", pgPassword);
}

// --- API ---
var api = (stack.IsLab && !isPublish
        // Lab: its own https port, not the launch profile's 5080 (which the dev stack owns).
        ? builder.AddProject<Projects.AgentOs_Api>("api", launchProfileName: null)
            .WithHttpsEndpoint(port: stack.Port(5080), name: "https")
            // No launch profile → set what it provided (otherwise the Api boots as Production and exits).
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        : builder.AddProject<Projects.AgentOs_Api>("api"))
    .WithReference(db).WaitFor(db)
    .WithReference(keycloak).WaitFor(keycloak)
    .WithEnvironment("Auth__Keycloak__Authority", stack.IsLab && !isPublish
        ? ReferenceExpression.Create($"{StackProfile.LabKeycloakOrigin}/realms/agentic")
        : RealmAuthority(keycloak.GetEndpoint("http")))
    .WithEnvironment("Auth__Keycloak__Audience", "agentic-api")
    .WithEnvironment("Auth__Keycloak__Admin__BaseUrl", keycloak.GetEndpoint("http"))
    .WithEnvironment("Auth__Keycloak__Admin__Realm", "agentic")
    .WithEnvironment("Auth__Keycloak__Admin__Username", kcAdminUsername)
    .WithEnvironment("Auth__Keycloak__Admin__Password", kcAdminPassword)
    .WithEnvironment("Auth__Keycloak__Admin__ClientId", "admin-cli")
    .WithEnvironment("Email__From", "noreply@agentic.local")
    .WithEnvironment("Email__FromName", "AgentOS")
    .WithEnvironment("Email__SmtpHost", smtpHost)
    .WithEnvironment("Email__SmtpPort", smtpPort);

if (!isPublish) { api.WaitFor(mailhog!); }
else { api.WithExternalHttpEndpoints(); }   // cloud: health smoke + Scalar need a public FQDN

// The Web must be reachable at EXACTLY https://localhost:5180 (lab: 6180) in run mode — both are hard-wired
// into the Keycloak realm's redirectUris, so any other scheme/port/origin breaks OIDC login. Make that
// binding the single source of truth, with no possibility of a port clash:
//   • launchProfileName: null     — ignore launchSettings (its "http" profile pins http://localhost:5180,
//                                    which otherwise wins and makes Kestrel serve plain HTTP → the
//                                    browser gets ERR_SSL_PROTOCOL_ERROR and OIDC builds a http://
//                                    redirect_uri the realm rejects ('Invalid parameter: redirect_uri').
//   • isProxied: false + port==targetPort  — Kestrel binds https://localhost:5180 DIRECTLY (no Aspire
//                                    reverse-proxy in front), so nothing else contends for 5180 and the
//                                    request origin the app sees IS https://localhost:5180 → the OIDC
//                                    redirect_uri matches the realm with no forwarded-header juggling.
var web = builder.AddProject<Projects.AgentOs_Web>("web", launchProfileName: null);
if (!isPublish)
{
    // Run mode: pin EXACTLY https://localhost:5180 — lab 6180 (see comment above).
    web.WithHttpsEndpoint(port: stack.Port(5180), targetPort: stack.Port(5180), name: "https", isProxied: false);
}
else
{
    // Cloud: TLS terminates at the ACA ingress — the container must listen on plain HTTP (an
    // in-container HTTPS endpoint would make Kestrel demand a server cert that doesn't exist in
    // the image and crash on boot). External ingress so the browser can reach the desktop.
    web.WithHttpEndpoint(name: "http", targetPort: 8080)
       .WithExternalHttpEndpoints();
}
web
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", isPublish ? "Production" : "Development")
    .WithEnvironment("Auth__DevAutoLogin", "false")
    .WithReference(db).WaitFor(db)
    .WithReference(keycloak).WaitFor(keycloak)
    // services__api__* — only so System → About can show the Api's version + health. The desktop runs every
    // module in-process and never calls the Api, so no WaitFor: the Web must not depend on the Api being up.
    .WithReference(api)
    .WithEnvironment("Auth__Keycloak__Authority", stack.IsLab && !isPublish
        ? ReferenceExpression.Create($"{StackProfile.LabKeycloakOrigin}/realms/agentic")
        : RealmAuthority(keycloak.GetEndpoint("http")))
    .WithEnvironment("Auth__Keycloak__Audience", "agentic-api")
    .WithEnvironment("Auth__Keycloak__ClientId", "agentic-web")
    .WithEnvironment("Auth__Keycloak__ClientSecret", kcWebClientSecret)
    .WithEnvironment("Auth__Keycloak__Admin__BaseUrl", keycloak.GetEndpoint("http"))
    .WithEnvironment("Auth__Keycloak__Admin__Realm", "agentic")
    .WithEnvironment("Auth__Keycloak__Admin__Username", kcAdminUsername)
    .WithEnvironment("Auth__Keycloak__Admin__Password", kcAdminPassword)
    .WithEnvironment("Auth__Keycloak__Admin__ClientId", "admin-cli")
    .WithEnvironment("Email__From", "noreply@agentic.local")
    .WithEnvironment("Email__FromName", "AgentOS")
    .WithEnvironment("Email__SmtpHost", smtpHost)
    .WithEnvironment("Email__SmtpPort", smtpPort);

if (!isPublish) { web.WaitFor(mailhog!); }

// Lab only: optional real LLM keys for live agent runs, seeded from the AppHost's user-secrets
// (Lab:Claude:ApiKey, Lab:AzureOpenAi:ApiKey / :Endpoint — see docs/LAB.md). They become the platform seed
// tier (env vars); a per-tenant key set in Settings still wins. Never set for the dev stack.
if (stack.IsLab && !isPublish)
{
    // Own cookie names too (defense in depth next to the own hostnames — see StackProfile).
    web.WithEnvironment("Auth__CookiePrefix", "agentic-lab");

    foreach (var (secret, env) in new[]
    {
        ("Lab:Claude:ApiKey", "Llm__Claude__ApiKey"),
        ("Lab:AzureOpenAi:ApiKey", "Llm__AzureOpenAi__ApiKey"),
        ("Lab:AzureOpenAi:Endpoint", "Llm__AzureOpenAi__Endpoint"),
    })
    {
        if (builder.Configuration[secret] is { Length: > 0 } value)
        {
            api.WithEnvironment(env, value);
            web.WithEnvironment(env, value);
        }
    }
}

builder.Build().Run();

// Build the Keycloak realm authority (<kc-url>/realms/agentic) as a deferred ReferenceExpression.
// Uses the explicit builder (AppendFormatted + AppendLiteral) rather than an interpolated string so the
// endpoint URL is captured as an IValueProvider — not stringified via Object.ToString() (cs/call-to-object-tostring).
static ReferenceExpression RealmAuthority(EndpointReference http)
{
    var b = new ReferenceExpressionBuilder();
    b.AppendFormatted(http.Property(EndpointProperty.Url));
    b.AppendLiteral("/realms/agentic");
    return b.Build();
}

// JDBC URL for Keycloak's durable store on the provisioned flexible server — composed from the
// server's bicep `hostName` output so it resolves at deploy time (publish mode only).
static ReferenceExpression KeycloakJdbcUrl(IResourceBuilder<AzurePostgresFlexibleServerResource> postgres)
{
    var b = new ReferenceExpressionBuilder();
    b.AppendLiteral("jdbc:postgresql://");
    b.AppendFormatted(postgres.GetOutput("hostName"));
    b.AppendLiteral(":5432/keycloak?sslmode=require");
    return b.Build();
}

// Host ports + volume names per stack. dev keeps the historic ports and Aspire's default volume names (no
// migration for existing dev data); lab shifts every host port by +1000 and uses its own named volumes.
internal sealed class StackProfile(string? name)
{
    /// <summary>
    /// Lab hostnames. Cookies are scoped to the host, not the port, so a lab on "localhost" would share one
    /// cookie jar with the dev stack, its Keycloak and every other local app — sessions overwrite each other
    /// and the jar overflows (HTTP 431, lost OIDC correlation cookies). *.dev.localhost names resolve to
    /// loopback in browsers and .NET with no hosts-file edit, and the ASP.NET dev certificate already covers
    /// *.dev.localhost, so the lab gets its own cookie jars for both the desktop and Keycloak.
    /// </summary>
    public const string LabWebOrigin = "https://lab.dev.localhost:6180";

    public const string LabKeycloakOrigin = "https://labkc.dev.localhost:9080";

    private const int LabPortOffset = 1000;

    public bool IsLab { get; } = string.Equals(name, "lab", StringComparison.OrdinalIgnoreCase);

    public int Port(int devPort) => IsLab ? devPort + LabPortOffset : devPort;

    /// <summary>null = Aspire's default (dev); "agentos-lab-&lt;suffix&gt;" for the lab stack.</summary>
    public string? Volume(string suffix) => IsLab ? $"agentos-lab-{suffix}" : null;
}
