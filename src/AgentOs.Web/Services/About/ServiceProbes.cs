// The probes behind System → About, one per service. Each asks the real service (no cached config echo):
// the Api's /version + /health, Keycloak's OIDC discovery + admin serverinfo, Postgres' catalog, the SMTP
// greeting. A service that isn't part of this run (standalone Web: no Api, no database, dev auto-login) is
// NotConfigured, not Down. Secrets never appear: connection strings are split into host/port/user only and
// keys are reported as "Configured" / "Not set".

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants.Email;
using AgentOs.Modules.Tenants.Keycloak;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AgentOs.Web.Services.About;

/// <summary>Shared identity + helpers for a probe.</summary>
public abstract class ServiceProbe(string id, string name, string role, string icon, int order) : IServiceProbe
{
    /// <inheritdoc />
    public int Order => order;

    /// <summary>A card for this service in <paramref name="health"/>.</summary>
    protected ServiceStatus Status(ServiceHealth health) => new(id, name, role, icon, health);

    /// <inheritdoc />
    public ServiceStatus Unreachable(string reason) => Status(ServiceHealth.Down) with
    {
        Summary = "Unreachable",
        Infra = [new("Error", About.Format.Clip(reason, 240))],
    };

    /// <inheritdoc />
    public abstract Task<ServiceStatus> ProbeAsync(CancellationToken ct);
}

/// <summary>Formatting shared by probes and the page.</summary>
public static class Format
{
    /// <summary>"512 B", "3.4 MB", "1.2 GB".</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{v:0.#} {units[i]}");
    }

    /// <summary>"42 s", "17 min", "3 h 05 min", "2 d 4 h".</summary>
    public static string Duration(TimeSpan t) => t.TotalSeconds < 60
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalSeconds} s")
        : t.TotalMinutes < 60 ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes} min")
        : t.TotalHours < 24 ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours} h {t.Minutes:00} min")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalDays} d {t.Hours} h");

    /// <summary>"7 ms", "1.24 s".</summary>
    public static string Latency(double ms) => ms < 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{ms:0} ms")
        : string.Create(CultureInfo.InvariantCulture, $"{ms / 1000:0.00} s");

    /// <summary>The version without the "+commit" build metadata ("1.2.0+abc" → "1.2.0").</summary>
    public static string ShortVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) { return "unknown"; }
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }

    /// <summary>Single line, at most <paramref name="max"/> characters.</summary>
    public static string Clip(string? text, int max)
    {
        var line = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : string.Concat(line.AsSpan(0, max - 1), "…");
    }

    /// <summary>Elapsed milliseconds since a <see cref="Stopwatch.GetTimestamp"/>.</summary>
    public static double Since(long timestamp) => Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

    /// <summary>Build metadata of an assembly's informational version, e.g. the commit sha.</summary>
    public static string? Commit(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) { return null; }
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0 || plus == version.Length - 1) { return null; }
        var sha = version[(plus + 1)..];
        return sha.Length > 12 ? sha[..12] : sha;
    }
}

/// <summary>This process — the Blazor Server host every desktop session runs in.</summary>
internal sealed class WebHostProbe : ServiceProbe
{
    private static readonly DateTimeOffset Started = ProcessStart();
    private readonly IHostEnvironment _env;
    private readonly DevModeState _dev;
    private readonly IConfiguration _config;
    private readonly CircuitCounter _circuits;
    private readonly IServer _server;
    private readonly TimeProvider _time;
    private readonly AgentOs.Modules.AppConfig.KeyRingProtection? _keyRing;

    public WebHostProbe(IHostEnvironment env, DevModeState dev, IConfiguration config, CircuitCounter circuits, IServer server, TimeProvider time,
        AgentOs.Modules.AppConfig.KeyRingProtection? keyRing = null)
        : base("web", "Web desktop", "Blazor Server host", "squares-four", 0)
    {
        _keyRing = keyRing;
        _env = env;
        _dev = dev;
        _config = config;
        _circuits = circuits;
        _server = server;
        _time = time;
    }

    /// <summary>The Web's informational version (e.g. "0.6.0+sha").</summary>
    public static string InformationalVersion { get; } =
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(WebHostProbe).Assembly)?.InformationalVersion ?? "0.0.0";

    public override Task<ServiceStatus> ProbeAsync(CancellationToken ct)
    {
        var gc = GC.GetGCMemoryInfo();
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var commit = Format.Commit(InformationalVersion);
        List<ServiceFact> facts =
        [
            new("Environment", _env.EnvironmentName),
            new("Runtime", RuntimeInformation.FrameworkDescription),
            new("Rendering", "Interactive Server · SignalR"),
            new("Sign-in", _dev.DevAutoLogin ? "Dev auto-login (Development only)" : "Keycloak OIDC · cookie session"),
            new("Uptime", Format.Duration(_time.GetUtcNow() - Started)),
            new("Open sessions", _circuits.Open.ToString(CultureInfo.InvariantCulture)),
            new("Memory", $"{Format.Bytes(Environment.WorkingSet)} working set · {Format.Bytes(gc.HeapSizeBytes)} GC heap"),
            new("GC", $"{(GCSettings.IsServerGC ? "Server" : "Workstation")} · {GCSettings.LatencyMode}"),
        ];
        if (commit is not null) { facts.Insert(0, new("Build", commit)); }
        // E0.2: the key ring encrypts every tenant secret; unwrapped outside Development is a finding.
        var unwrappedInProd = _keyRing is { IsWrapped: false } && !_env.IsDevelopment();
        facts.Add(new("Key ring", _keyRing switch
        {
            null => "—",
            { IsWrapped: true } k => $"Wrapped · {k.Mode}",
            _ => unwrappedInProd ? "Unwrapped — set DataProtection:KeyVaultKeyId or a certificate" : "Unwrapped (fine in Development)",
        }));

        return Task.FromResult(Status(unwrappedInProd ? ServiceHealth.Degraded : ServiceHealth.Healthy) with
        {
            Version = Format.ShortVersion(InformationalVersion),
            Summary = $"{_env.EnvironmentName} · {Environment.ProcessorCount} CPUs",
            Facts = facts,
            Infra =
            [
                new("Listening", addresses.Count == 0 ? "—" : string.Join(", ", addresses)),
                new("Process", string.Create(CultureInfo.InvariantCulture, $"pid {Environment.ProcessId} · {ThreadPool.ThreadCount} pool threads")),
                new("OS", RuntimeInformation.OSDescription),
                new("Machine", Environment.MachineName),
                new("Cookie prefix", _config["Auth:CookiePrefix"] is { Length: > 0 } p ? p : "agentic (default)"),
                new("Key ring protection", _keyRing?.Detail ?? "none"),
            ],
        });
    }

    private static DateTimeOffset ProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }
}

/// <summary>The REST + MCP backend. Wired by the AppHost (<c>services:api:*</c>) or <c>Api:BaseUrl</c>.</summary>
internal sealed class ApiProbe(IConfiguration config, ProbeHttpClient http)
    : ServiceProbe("api", "Api", "REST + MCP backend", "terminal", 1)
{
    private static readonly string[] ApiAddressKeys = ["services:api:https:0", "services:api:http:0", "Api:BaseUrl"];

    /// <summary>The Api base address: Aspire service discovery keys first, then an explicit override.</summary>
    internal static Uri? BaseAddress(IConfiguration config)
    {
        foreach (var key in ApiAddressKeys)
        {
            if (Uri.TryCreate(config[key], UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                return uri;
            }
        }
        return null;
    }

    public override async Task<ServiceStatus> ProbeAsync(CancellationToken ct)
    {
        if (BaseAddress(config) is not { } baseUri)
        {
            return Status(ServiceHealth.NotConfigured) with
            {
                Summary = "Not part of this run — the desktop runs every module in-process",
            };
        }

        var client = http.Client;
        var versionTask = GetJsonAsync(client, new Uri(baseUri, "/version"), ct);
        var started = Stopwatch.GetTimestamp();
        using var health = await client.GetAsync(new Uri(baseUri, "/health"), ct).ConfigureAwait(false);
        var latency = Format.Since(started);
        var report = await ReadHealthAsync(health, ct).ConfigureAwait(false);
        using var version = await versionTask.ConfigureAwait(false);

        var apiVersion = version?.RootElement.TryGetProperty("version", out var v) == true ? v.GetString() : null;
        var overall = report.Status switch
        {
            "Healthy" => ServiceHealth.Healthy,
            "Degraded" => ServiceHealth.Degraded,
            _ => ServiceHealth.Down,
        };
        List<ServiceFact> facts = [new("Readiness", report.Status ?? $"HTTP {(int)health.StatusCode}")];
        facts.AddRange(report.Checks.Select(c => new ServiceFact($"Check · {c.Name}", c.Status)));
        if (Format.Commit(apiVersion) is { } commit) { facts.Insert(0, new("Build", commit)); }

        return Status(overall) with
        {
            Version = apiVersion is null ? null : Format.ShortVersion(apiVersion),
            LatencyMs = latency,
            Summary = report.Checks.Count == 0 ? "Answering" : $"{report.Checks.Count(c => c.Status == "Healthy")}/{report.Checks.Count} checks healthy",
            Facts = facts,
            Infra = [new("Base URL", baseUri.GetLeftPart(UriPartial.Authority)), new("Scalar UI", new Uri(baseUri, "/scalar/v1").ToString())],
        };
    }

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        using var resp = await client.GetAsync(uri, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) { return null; }
        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    internal sealed record HealthCheckLine(string Name, string Status);

    internal sealed record HealthReportView(string? Status, IReadOnlyList<HealthCheckLine> Checks);

    /// <summary>Reads the ServiceDefaults health JSON: <c>{ status, entries: { name: { status } } }</c>.</summary>
    internal static async Task<HealthReportView> ReadHealthAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                return ParseHealth(doc.RootElement);
            }
        }
        catch (JsonException)
        {
            return new HealthReportView(null, []);
        }
    }

    internal static HealthReportView ParseHealth(JsonElement root)
    {
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        var checks = new List<HealthCheckLine>();
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in entries.EnumerateObject())
            {
                var entryStatus = entry.Value.TryGetProperty("status", out var es) ? es.GetString() : null;
                checks.Add(new HealthCheckLine(entry.Name, entryStatus ?? "Unknown"));
            }
        }
        return new HealthReportView(status, checks);
    }
}

/// <summary>The identity server: OIDC discovery (what the browser signs in through) + the admin API version.</summary>
internal sealed class KeycloakProbe(IConfiguration config, DevModeState dev, ProbeHttpClient http, IServiceScopeFactory scopes)
    : ServiceProbe("keycloak", "Keycloak", "Identity server (OIDC)", "lock", 2)
{
    /// <summary>Discovery document URL — the backend-reachable MetadataAddress wins over the public authority,
    /// matching the readiness health check.</summary>
    internal static Uri? DiscoveryUrl(IConfiguration config)
    {
        var metadata = config["Auth:Keycloak:MetadataAddress"];
        var authority = config["Auth:Keycloak:Authority"];
        var url = !string.IsNullOrWhiteSpace(metadata) ? metadata
            : string.IsNullOrWhiteSpace(authority) ? null
            : authority.TrimEnd('/') + "/.well-known/openid-configuration";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    public override async Task<ServiceStatus> ProbeAsync(CancellationToken ct)
    {
        if (DiscoveryUrl(config) is not { } discovery)
        {
            return Status(ServiceHealth.NotConfigured) with
            {
                Summary = dev.DevAutoLogin
                    ? "Dev auto-login — no identity server in this run"
                    : "No authority configured (Auth:Keycloak:Authority)",
            };
        }

        var versionTask = ServerVersionAsync(ct);
        var client = http.Client;
        var started = Stopwatch.GetTimestamp();
        using var resp = await client.GetAsync(discovery, ct).ConfigureAwait(false);
        var latency = Format.Since(started);
        if (!resp.IsSuccessStatusCode)
        {
            return Unreachable($"Discovery returned HTTP {(int)resp.StatusCode}") with { LatencyMs = latency };
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        OidcDiscovery oidc;
        await using (stream.ConfigureAwait(false))
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            oidc = OidcDiscovery.Parse(doc.RootElement);
        }
        var (version, versionError) = await versionTask.ConfigureAwait(false);

        List<ServiceFact> infra =
        [
            new("Issuer", oidc.Issuer ?? "—"),
            new("Discovery", discovery.ToString()),
            new("Admin API", config["Auth:Keycloak:Admin:BaseUrl"] is { Length: > 0 } admin ? admin : "not configured"),
        ];
        if (versionError is not null) { infra.Add(new("Admin API error", Format.Clip(versionError, 200))); }

        return Status(oidc.Issuer is null ? ServiceHealth.Degraded : ServiceHealth.Healthy) with
        {
            Version = version,
            LatencyMs = latency,
            Summary = $"Realm {oidc.Realm ?? "?"} · {(oidc.SupportsPar ? "PAR + " : string.Empty)}{(oidc.SupportsPkceS256 ? "PKCE" : "no PKCE")}",
            Facts =
            [
                new("Realm", oidc.Realm ?? "unknown"),
                new("Protocol", "OpenID Connect · authorization code"),
                new("Pushed auth requests", oidc.SupportsPar ? "Supported (used)" : "Not offered"),
                new("PKCE", oidc.SupportsPkceS256 ? "S256" : "Not offered"),
                new("Token signing", oidc.SigningAlgorithms.Count == 0 ? "unknown" : string.Join(", ", oidc.SigningAlgorithms.Take(3))),
                new("Web client", config["Auth:Keycloak:ClientId"] is { Length: > 0 } c ? c : "agentic-web"),
            ],
            Infra = infra,
        };
    }

    private async Task<(string? Version, string? Error)> ServerVersionAsync(CancellationToken ct)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var admin = scope.ServiceProvider.GetRequiredService<IKeycloakAdminClient>();
                return (await admin.GetServerVersionAsync(ct).ConfigureAwait(false), null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>The parts of an OIDC discovery document the About card shows.</summary>
    internal sealed record OidcDiscovery(string? Issuer, string? Realm, bool SupportsPar, bool SupportsPkceS256, IReadOnlyList<string> SigningAlgorithms)
    {
        public static OidcDiscovery Parse(JsonElement root)
        {
            var issuer = root.TryGetProperty("issuer", out var i) ? i.GetString() : null;
            var realm = issuer is null ? null : RealmOf(issuer);
            var par = root.TryGetProperty("pushed_authorization_request_endpoint", out var p) && p.ValueKind == JsonValueKind.String;
            var pkce = Strings(root, "code_challenge_methods_supported").Contains("S256", StringComparer.Ordinal);
            return new OidcDiscovery(issuer, realm, par, pkce, Strings(root, "id_token_signing_alg_values_supported"));
        }

        /// <summary>".../realms/agentic" → "agentic".</summary>
        internal static string? RealmOf(string issuer)
        {
            var marker = issuer.LastIndexOf("/realms/", StringComparison.Ordinal);
            return marker < 0 ? null : issuer[(marker + "/realms/".Length)..].TrimEnd('/');
        }

        private static List<string> Strings(JsonElement root, string name)
            => root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? [.. arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                : [];
    }
}

/// <summary>The platform database: server version, size, pgvector, and every module's migration history.</summary>
internal sealed class PostgresProbe(IConfiguration config)
    : ServiceProbe("postgres", "PostgreSQL", "Database (one schema per module)", "floppy-disk", 3)
{
    private const string ServerSql = """
        select current_setting('server_version'), current_database(), pg_database_size(current_database()),
               (select extversion from pg_extension where extname = 'vector'),
               (select count(*) from pg_stat_activity where datname = current_database()),
               current_setting('max_connections')
        """;

    // Every module owns its migration history table inside its own schema (pipeline.__EFMigrationsHistory,
    // config.__DataProtectionMigrationsHistory, …) — discovered, so a new module shows up with no code change.
    private const string HistoriesSql = """
        select table_schema, table_name from information_schema.tables
        where table_name like '\_\_%MigrationsHistory' order by 1, 2
        """;

    public override async Task<ServiceStatus> ProbeAsync(CancellationToken ct)
    {
        var cs = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return Status(ServiceHealth.NotConfigured) with
            {
                Summary = "No database — modules run on in-memory no-op stores",
            };
        }

        var started = Stopwatch.GetTimestamp();
        await using var conn = new NpgsqlConnection(cs);
                {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            string serverVersion, database, pgvector, maxConnections;
            long size, connections;
            var histories = new List<(string Schema, string Table)>();
            await using var batch = new NpgsqlBatch(conn) { BatchCommands = { new(ServerSql), new(HistoriesSql) } };
                        {
                var reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    await reader.ReadAsync(ct).ConfigureAwait(false);
                    serverVersion = reader.GetString(0);
                    database = reader.GetString(1);
                    size = reader.GetInt64(2);
                    pgvector = reader.IsDBNull(3) ? "not installed" : reader.GetString(3);
                    connections = reader.GetInt64(4);
                    maxConnections = reader.GetString(5);
                    await reader.NextResultAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        histories.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }
            }
            var latency = Format.Since(started);
            var migrations = await MigrationFactsAsync(conn, histories, ct).ConfigureAwait(false);

            var b = new NpgsqlConnectionStringBuilder(cs);
            List<ServiceFact> facts =
            [
                new("Database", database),
                new("Size", Format.Bytes(size)),
                new("Connections", $"{connections.ToString(CultureInfo.InvariantCulture)} of {maxConnections}"),
                new("pgvector", pgvector),
            ];
            facts.AddRange(migrations);

            return Status(ServiceHealth.Healthy) with
            {
                Version = ServerVersionShort(serverVersion),
                LatencyMs = latency,
                Summary = $"{histories.Count} module schemas · {Format.Bytes(size)}",
                Facts = facts,
                Infra =
                [
                    new("Host", string.Create(CultureInfo.InvariantCulture, $"{b.Host}:{b.Port}")),
                    new("User", b.Username ?? "—"),
                    new("SSL mode", b.SslMode.ToString()),
                    new("Pool", string.Create(CultureInfo.InvariantCulture, $"{b.MinPoolSize}–{b.MaxPoolSize} connections")),
                    new("Server build", Format.Clip(serverVersion, 120)),
                ],
            };
        }
    }

    /// <summary>One fact per history table: "pipeline" → "14 migrations · latest AddCacheTokens".</summary>
    private static async Task<List<ServiceFact>> MigrationFactsAsync(NpgsqlConnection conn, List<(string Schema, string Table)> histories, CancellationToken ct)
    {
        var facts = new List<ServiceFact>(histories.Count);
        if (histories.Count == 0) { return facts; }

        // One round-trip for all tables. Identifiers come from the catalog and are quoted (" doubled).
        await using var batch = new NpgsqlBatch(conn);
                {
            foreach (var (schema, table) in histories)
            {
                batch.BatchCommands.Add(new NpgsqlBatchCommand(
                    $"select count(*), max(\"MigrationId\") from {Quote(schema)}.{Quote(table)}"));
            }
            var reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                foreach (var (schema, table) in histories)
                {
                    await reader.ReadAsync(ct).ConfigureAwait(false);
                    var count = reader.GetInt64(0);
                    var latest = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var label = table == "__EFMigrationsHistory" ? $"Schema · {schema}" : $"Schema · {schema} ({HistoryKind(table)})";
                    facts.Add(new ServiceFact(label, MigrationSummary(count, latest)));
                    await reader.NextResultAsync(ct).ConfigureAwait(false);
                }
            }
        }
        return facts;
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>"__DataProtectionMigrationsHistory" → "data protection".</summary>
    internal static string HistoryKind(string table)
    {
        var core = table.Trim('_');
        core = core.EndsWith("MigrationsHistory", StringComparison.Ordinal) ? core[..^"MigrationsHistory".Length] : core;
        var sb = new StringBuilder(core.Length + 4);
        foreach (var ch in core)
        {
            if (char.IsUpper(ch) && sb.Length > 0) { sb.Append(' '); }
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.Length == 0 ? "history" : sb.ToString();
    }

    /// <summary>"20261017125743_AddCacheTokens" → "14 migrations · latest AddCacheTokens".</summary>
    internal static string MigrationSummary(long count, string? latestId)
    {
        var plural = count == 1 ? "migration" : "migrations";
        if (string.IsNullOrEmpty(latestId)) { return string.Create(CultureInfo.InvariantCulture, $"{count} {plural}"); }
        var underscore = latestId.IndexOf('_', StringComparison.Ordinal);
        var name = underscore >= 0 && underscore < latestId.Length - 1 ? latestId[(underscore + 1)..] : latestId;
        return string.Create(CultureInfo.InvariantCulture, $"{count} {plural} · latest {name}");
    }

    /// <summary>"18.0 (Debian 18.0-1.pgdg13+1)" → "18.0".</summary>
    internal static string ServerVersionShort(string version)
    {
        var space = version.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? version[..space] : version;
    }
}

/// <summary>The outbound mail server used for invitations and password resets: an SMTP greeting round-trip.</summary>
internal sealed class SmtpProbe(IOptions<EmailOptions> options)
    : ServiceProbe("smtp", "SMTP", "Invitation + reset email", "bell", 4)
{
    public override async Task<ServiceStatus> ProbeAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.SmtpHost))
        {
            return Status(ServiceHealth.NotConfigured) with
            {
                Summary = "Email disabled — invitations show a copyable link instead",
            };
        }

        var started = Stopwatch.GetTimestamp();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(o.SmtpHost, o.SmtpPort, ct).ConfigureAwait(false);
        var stream = tcp.GetStream();
        string? greeting;
        using (var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            greeting = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        }
        var latency = Format.Since(started);
        await stream.WriteAsync("QUIT\r\n"u8.ToArray(), ct).ConfigureAwait(false);

        var ok = greeting?.StartsWith("220", StringComparison.Ordinal) == true;
        return Status(ok ? ServiceHealth.Healthy : ServiceHealth.Degraded) with
        {
            Version = Product(greeting),
            LatencyMs = latency,
            Summary = ok ? "Accepting mail" : "Unexpected greeting",
            Facts =
            [
                new("Sender", string.IsNullOrWhiteSpace(o.FromName) ? o.From : $"{o.FromName} <{o.From}>"),
                new("STARTTLS", o.UseStartTls ? "Required" : "Off"),
                new("Authentication", string.IsNullOrWhiteSpace(o.User) ? "None" : "Username + password"),
            ],
            Infra =
            [
                new("Server", string.Create(CultureInfo.InvariantCulture, $"{o.SmtpHost}:{o.SmtpPort}")),
                new("Greeting", Format.Clip(greeting, 160)),
            ],
        };
    }

    private static readonly string[] KnownSmtpProducts = ["MailHog", "Mailpit", "Postfix", "Exim", "Microsoft ESMTP", "Sendmail", "Amazon SES", "SendGrid"];

    /// <summary>Well-known server names from the 220 greeting ("… ESMTP MailHog" → "MailHog").</summary>
    internal static string? Product(string? greeting)
    {
        if (string.IsNullOrEmpty(greeting)) { return null; }
        foreach (var name in KnownSmtpProducts)
        {
            if (greeting.Contains(name, StringComparison.OrdinalIgnoreCase)) { return name; }
        }
        return greeting.Contains("ESMTP", StringComparison.Ordinal) ? "ESMTP" : null;
    }
}
