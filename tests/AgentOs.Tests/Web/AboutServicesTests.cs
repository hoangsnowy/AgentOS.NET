// System → About: the service-status model, the cached single-flight aggregator, every probe's parsing and
// "not part of this run" path, a real SMTP round-trip against an in-process greeter, the per-tenant LLM card,
// the copyable report (infra only for platform admins), and the rendered page (cards, map, no phantom CSS).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Llm;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Tenants.Email;
using AgentOs.Web.Components.UI;
using AgentOs.Web.Services;
using AgentOs.Web.Services.About;
using AgentOs.Web.Shell.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed partial class AboutServicesTests
{
    // ---- model ---------------------------------------------------------------------------------------

    private static ServiceStatus S(string id, ServiceHealth h) => new(id, id, "role", "info", h);

    [Fact]
    public void Worst_IgnoresNotConfigured_AndDownBeatsDegraded()
    {
        SystemSnapshot.Worst([S("a", ServiceHealth.Healthy), S("b", ServiceHealth.NotConfigured)]).ShouldBe(ServiceHealth.Healthy);
        SystemSnapshot.Worst([S("a", ServiceHealth.Degraded), S("b", ServiceHealth.Healthy)]).ShouldBe(ServiceHealth.Degraded);
        SystemSnapshot.Worst([S("a", ServiceHealth.Degraded), S("b", ServiceHealth.Down)]).ShouldBe(ServiceHealth.Down);
        SystemSnapshot.Worst([]).ShouldBe(ServiceHealth.Healthy);
    }

    [Fact]
    public void Headline_CountsDegradedAndDown()
    {
        SystemSnapshot.Headline([S("a", ServiceHealth.Healthy), S("b", ServiceHealth.NotConfigured)]).ShouldBe("All systems operational");
        SystemSnapshot.Headline([S("a", ServiceHealth.Degraded)]).ShouldBe("1 degraded");
        SystemSnapshot.Headline([S("a", ServiceHealth.Down), S("b", ServiceHealth.Down)]).ShouldBe("2 down");
        SystemSnapshot.Headline([S("a", ServiceHealth.Degraded), S("b", ServiceHealth.Down)]).ShouldBe("1 degraded · 1 down");
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    public void Format_Bytes(long bytes, string expected) => Format.Bytes(bytes).ShouldBe(expected);

    [Fact]
    public void Format_DurationLatencyVersion()
    {
        Format.Duration(TimeSpan.FromSeconds(42)).ShouldBe("42 s");
        Format.Duration(TimeSpan.FromMinutes(17)).ShouldBe("17 min");
        Format.Duration(TimeSpan.FromMinutes(185)).ShouldBe("3 h 05 min");
        Format.Duration(TimeSpan.FromHours(52)).ShouldBe("2 d 4 h");
        Format.Latency(7.4).ShouldBe("7 ms");
        Format.Latency(1240).ShouldBe("1.24 s");
        Format.ShortVersion("1.2.0+abcdef0123456789").ShouldBe("1.2.0");
        Format.ShortVersion(null).ShouldBe("unknown");
        Format.Commit("1.2.0+abcdef0123456789").ShouldBe("abcdef012345");
        Format.Commit("1.2.0").ShouldBeNull();
        Format.Clip("line one\nline two", 9).ShouldBe("line one…");
    }

    // ---- aggregator ----------------------------------------------------------------------------------

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeProbe(string id, int order, Func<CancellationToken, Task<ServiceStatus>> run) : IServiceProbe
    {
        public int Calls;
        public int Order => order;
        public ServiceStatus Unreachable(string reason) => new ServiceStatus(id, id, "r", "info", ServiceHealth.Down) { Infra = [new("Error", reason)] };
        public Task<ServiceStatus> ProbeAsync(CancellationToken ct) { Interlocked.Increment(ref Calls); return run(ct); }
    }

    private static SystemStatusService Service(TimeProvider clock, params IServiceProbe[] probes)
        => new(probes, clock, NullLogger<SystemStatusService>.Instance, TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task GetAsync_OrdersByProbeOrder_AndCachesWithinTtl()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var b = new FakeProbe("b", 2, _ => Task.FromResult(S("b", ServiceHealth.Healthy)));
        var a = new FakeProbe("a", 1, _ => Task.FromResult(S("a", ServiceHealth.Healthy)));
        var svc = Service(clock, b, a);

        var first = await svc.GetAsync(ct: TestContext.Current.CancellationToken);
        first.Services.Select(s => s.Id).ShouldBe(["a", "b"]);

        clock.Now += SystemStatusService.Ttl - TimeSpan.FromSeconds(1);
        (await svc.GetAsync(ct: TestContext.Current.CancellationToken)).ShouldBeSameAs(first);
        a.Calls.ShouldBe(1);

        clock.Now += TimeSpan.FromSeconds(2);
        (await svc.GetAsync(ct: TestContext.Current.CancellationToken)).ShouldNotBeSameAs(first);
        a.Calls.ShouldBe(2);
        svc.Last.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetAsync_Refresh_SkipsCache_ButConcurrentCallersShareOneRun()
    {
        var gate = new TaskCompletionSource();
        var probe = new FakeProbe("a", 1, async _ => { await gate.Task; return S("a", ServiceHealth.Healthy); });
        var svc = Service(TimeProvider.System, probe);

        var calls = Enumerable.Range(0, 10).Select(_ => svc.GetAsync(refresh: true, TestContext.Current.CancellationToken)).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        probe.Calls.ShouldBe(1);
        results.Distinct().Count().ShouldBe(1);

        await svc.GetAsync(refresh: true, TestContext.Current.CancellationToken);
        probe.Calls.ShouldBe(2);   // a completed run is replaced, not reused
    }

    [Fact]
    public async Task GetAsync_ThrowingOrHangingProbe_IsDown_OthersStillReport()
    {
        var ok = new FakeProbe("ok", 0, _ => Task.FromResult(S("ok", ServiceHealth.Healthy)));
        var boom = new FakeProbe("boom", 1, _ => throw new InvalidOperationException("connection refused"));
        var hang = new FakeProbe("hang", 2, async ct => { await Task.Delay(Timeout.Infinite, ct); return S("hang", ServiceHealth.Healthy); });

        var snap = await Service(TimeProvider.System, ok, boom, hang).GetAsync(ct: TestContext.Current.CancellationToken);

        snap.Services.Select(s => s.Health).ShouldBe([ServiceHealth.Healthy, ServiceHealth.Down, ServiceHealth.Down]);
        snap.Services[1].Infra.Single().Value.ShouldBe("connection refused");
        snap.Services[2].Infra.Single().Value.ShouldStartWith("Timed out");
        snap.Overall.ShouldBe(ServiceHealth.Down);
    }

    [Fact]
    public async Task GetAsync_CallerCancellation_DoesNotCancelTheSharedRun()
    {
        var gate = new TaskCompletionSource();
        var probe = new FakeProbe("a", 1, async _ => { await gate.Task; return S("a", ServiceHealth.Healthy); });
        var svc = Service(TimeProvider.System, probe);
        using var cts = new CancellationTokenSource();

        var cancelled = svc.GetAsync(refresh: true, cts.Token);
        var other = svc.GetAsync(refresh: true, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => cancelled);

        gate.SetResult();
        (await other).Services.Single().Health.ShouldBe(ServiceHealth.Healthy);
    }

    // ---- probes: "not part of this run" --------------------------------------------------------------

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public async Task Probes_WithoutWiring_AreNotConfigured_NotDown()
    {
        using var http = new ProbeHttpClient();
        var empty = Config();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        (await new ApiProbe(empty, http).ProbeAsync(TestContext.Current.CancellationToken)).Health.ShouldBe(ServiceHealth.NotConfigured);
        var kc = await new KeycloakProbe(empty, new DevModeState(true), http, scopes).ProbeAsync(TestContext.Current.CancellationToken);
        kc.Health.ShouldBe(ServiceHealth.NotConfigured);
        kc.Summary!.ShouldContain("Dev auto-login");
        (await new PostgresProbe(empty).ProbeAsync(TestContext.Current.CancellationToken)).Health.ShouldBe(ServiceHealth.NotConfigured);
        (await new SmtpProbe(Options.Create(new EmailOptions())).ProbeAsync(TestContext.Current.CancellationToken)).Health.ShouldBe(ServiceHealth.NotConfigured);
    }

    [Fact]
    public void ApiBaseAddress_PrefersHttpsServiceDiscovery_ThenHttp_ThenOverride()
    {
        ApiProbe.BaseAddress(Config(("services:api:http:0", "http://api:8080"), ("services:api:https:0", "https://localhost:5080")))!
            .ToString().ShouldBe("https://localhost:5080/");
        ApiProbe.BaseAddress(Config(("services:api:http:0", "http://api:8080"), ("Api:BaseUrl", "https://x")))!
            .ToString().ShouldBe("http://api:8080/");
        ApiProbe.BaseAddress(Config(("Api:BaseUrl", "https://api.example.com")))!.Host.ShouldBe("api.example.com");
        ApiProbe.BaseAddress(Config(("Api:BaseUrl", "ftp://nope"))).ShouldBeNull();
    }

    [Fact]
    public void ParseHealth_ReadsServiceDefaultsShape()
    {
        using var doc = JsonDocument.Parse("""{"status":"Degraded","entries":{"self":{"status":"Healthy"},"postgres":{"status":"Unhealthy","description":"x"}}}""");
        var report = ApiProbe.ParseHealth(doc.RootElement);
        report.Status.ShouldBe("Degraded");
        report.Checks.ShouldBe([new("self", "Healthy"), new("postgres", "Unhealthy")]);
    }

    [Fact]
    public void Keycloak_DiscoveryUrl_AndDocumentParsing()
    {
        KeycloakProbe.DiscoveryUrl(Config(("Auth:Keycloak:Authority", "https://kc/realms/agentic/")))!
            .ToString().ShouldBe("https://kc/realms/agentic/.well-known/openid-configuration");
        KeycloakProbe.DiscoveryUrl(Config(("Auth:Keycloak:Authority", "https://kc/realms/agentic"), ("Auth:Keycloak:MetadataAddress", "http://kc-internal/meta")))!
            .ToString().ShouldBe("http://kc-internal/meta");

        using var doc = JsonDocument.Parse("""
            {"issuer":"https://labkc.dev.localhost:9080/realms/agentic","pushed_authorization_request_endpoint":"https://x/par",
             "code_challenge_methods_supported":["plain","S256"],"id_token_signing_alg_values_supported":["RS256","ES256"]}
            """);
        var oidc = KeycloakProbe.OidcDiscovery.Parse(doc.RootElement);
        oidc.Realm.ShouldBe("agentic");
        oidc.SupportsPar.ShouldBeTrue();
        oidc.SupportsPkceS256.ShouldBeTrue();
        oidc.SigningAlgorithms.ShouldBe(["RS256", "ES256"]);

        using var bare = JsonDocument.Parse("{}");
        KeycloakProbe.OidcDiscovery.Parse(bare.RootElement).SupportsPar.ShouldBeFalse();
    }

    [Fact]
    public void Postgres_Helpers()
    {
        PostgresProbe.Quote("pipe\"line").ShouldBe("\"pipe\"\"line\"");
        PostgresProbe.HistoryKind("__DataProtectionMigrationsHistory").ShouldBe("data protection");
        PostgresProbe.MigrationSummary(14, "20261017125743_AddCacheTokens").ShouldBe("14 migrations · latest AddCacheTokens");
        PostgresProbe.MigrationSummary(1, null).ShouldBe("1 migration");
        PostgresProbe.ServerVersionShort("18.0 (Debian 18.0-1.pgdg13+1)").ShouldBe("18.0");
    }

    [Theory]
    [InlineData("220 mailhog.example ESMTP MailHog", "MailHog")]
    [InlineData("220 mx.example.com ESMTP Postfix (Ubuntu)", "Postfix")]
    [InlineData("220 smtp.example ESMTP ready", "ESMTP")]
    [InlineData(null, null)]
    public void Smtp_Product_FromGreeting(string? greeting, string? product) => SmtpProbe.Product(greeting).ShouldBe(product);

    [Fact]
    public async Task Smtp_RealRoundTrip_AgainstInProcessGreeter()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            await stream.WriteAsync("220 test.local ESMTP MailHog\r\n"u8.ToArray(), ct);
            var buf = new byte[64];
            var n = await stream.ReadAsync(buf, ct);
            return Encoding.ASCII.GetString(buf, 0, n);
        }, ct);

        var status = await new SmtpProbe(Options.Create(new EmailOptions { SmtpHost = "127.0.0.1", SmtpPort = port, From = "noreply@x" }))
            .ProbeAsync(ct);

        status.Health.ShouldBe(ServiceHealth.Healthy);
        status.Version.ShouldBe("MailHog");
        status.LatencyMs.ShouldNotBeNull();
        (await server).ShouldStartWith("QUIT");   // polite hang-up, no mail sent
    }

    // ---- LLM card + report ---------------------------------------------------------------------------

    private static LlmEffectiveConfig Llm(bool claudeKey, bool offline, string? force = null)
        => new("Claude", force, force is null ? ForceSource.None : ForceSource.RuntimeOverride, claudeKey, false, null) { OfflineFallback = offline };

    [Fact]
    public void LlmCard_HealthFollowsKeyAndOfflineFallback()
    {
        LlmGatewayCard.From(Llm(claudeKey: true, offline: false)).Health.ShouldBe(ServiceHealth.Healthy);
        var canned = LlmGatewayCard.From(Llm(claudeKey: false, offline: true));
        canned.Health.ShouldBe(ServiceHealth.Degraded);
        canned.Facts.Single(f => f.Label == "Mode").Value.ShouldContain("not billed");   // never shown as real usage
        LlmGatewayCard.From(Llm(claudeKey: false, offline: false)).Health.ShouldBe(ServiceHealth.Down);
        LlmGatewayCard.From(Llm(claudeKey: true, offline: false, force: "AzureOpenAI")).Health.ShouldBe(ServiceHealth.Down);
    }

    [Fact]
    public void LlmCard_NeverContainsKeyMaterial()
    {
        var o = new LlmOptions();
        o.Claude.ApiKey = "sk-ant-secret-value";
        var card = LlmGatewayCard.From(LlmConfigView.Combine(o, null, null, null, null));
        AboutReport.Text([card], DateTimeOffset.UnixEpoch, includeInfra: true).ShouldNotContain("sk-ant");
    }

    [Fact]
    public void Report_IncludesInfraOnlyWhenAllowed()
    {
        var s = S("api", ServiceHealth.Healthy) with { Version = "1.0", LatencyMs = 12, Facts = [new("Readiness", "Healthy")], Infra = [new("Base URL", "https://internal:5080")] };
        var member = AboutReport.Text([s], DateTimeOffset.UnixEpoch, includeInfra: false);
        member.ShouldContain("api — Healthy · 1.0 · 12 ms");
        member.ShouldContain("Readiness: Healthy");
        member.ShouldNotContain("internal:5080");
        AboutReport.Text([s], DateTimeOffset.UnixEpoch, includeInfra: true).ShouldContain("Base URL: https://internal:5080");
    }

    [Theory]
    [InlineData("18.3", "v18.3")]
    [InlineData("MailHog", "MailHog")]
    public void VersionLabel_PrefixesOnlyNumericVersions(string version, string label)
        => AboutServices.VersionLabel(version).ShouldBe(label);

    // ---- rendered page -------------------------------------------------------------------------------

    private sealed class StaticAuth(System.Security.Claims.ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private static async Task<string> RenderAsync(bool platformAdmin)
    {
        var probes = new IServiceProbe[]
        {
            new FakeProbe("web", 0, _ => Task.FromResult(new ServiceStatus("web", "Web desktop", "Blazor Server host", "squares-four", ServiceHealth.Healthy) { Version = "0.9.0" })),
            new FakeProbe("api", 1, _ => Task.FromResult(new ServiceStatus("api", "Api", "REST + MCP backend", "terminal", ServiceHealth.Down) { Infra = [new("Error", "refused")] })),
            new FakeProbe("postgres", 3, _ => Task.FromResult(new ServiceStatus("postgres", "PostgreSQL", "Database", "floppy-disk", ServiceHealth.NotConfigured))),
        };
        var services = new ServiceCollection()
            .AddSingleton(Service(TimeProvider.System, probes))
            .AddSingleton(new LlmConfigView(Substitute.For<IRuntimeOverrides>(), Options.Create(new LlmOptions())))
            .AddSingleton(Substitute.For<IJSRuntime>())
            .AddSingleton(new ToastService())
            .BuildServiceProvider();
        await using var _ = services;
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);

        var roles = platformAdmin ? new[] { "member", "platform-admin" } : ["member"];
        var identity = new System.Security.Claims.ClaimsIdentity(
            [new("tenant", "t1"), .. roles.Select(r => new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, r))],
            "test", System.Security.Claims.ClaimTypes.Name, System.Security.Claims.ClaimTypes.Role);
        var auth = new StaticAuth(new System.Security.Claims.ClaimsPrincipal(identity));

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<AboutHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(AboutHost.State)] = auth.GetAuthenticationStateAsync(),
            }));
            await output.QuiescenceTask;
            return WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    /// <summary>Supplies the cascading auth state the page reads, without the full auth pipeline.</summary>
    private sealed class AboutHost : ComponentBase
    {
        [Parameter] public Task<AuthenticationState>? State { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            builder.AddComponentParameter(1, "Value", State);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(b =>
            {
                b.OpenComponent<AboutServices>(0);
                b.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    [Fact]
    public async Task Page_RendersEveryServiceAndTheMap_InfraOnlyForPlatformAdmins()
    {
        var html = await RenderAsync(platformAdmin: false);
        foreach (var id in new[] { "web", "api", "postgres", "llm" })
        {
            html.ShouldContain($"data-service=\"{id}\"");
        }
        html.ShouldContain("role=\"img\"");                     // the service map
        html.ShouldContain("Api (Down)");                        // its accessible label names each state
        html.ShouldContain("2 down");                            // headline: Api + the keyless LLM gateway
        html.ShouldContain("Not in this run");
        html.ShouldNotContain("Infrastructure");
        html.ShouldNotContain("refused");

        var admin = await RenderAsync(platformAdmin: true);
        admin.ShouldContain("Infrastructure");
        admin.ShouldContain("refused");
    }

    [Fact]
    public async Task Page_UsesOnlyDefinedCssClasses()
    {
        var html = await RenderAsync(platformAdmin: true);
        var css = await File.ReadAllTextAsync(AppCss(), TestContext.Current.CancellationToken);
        var used = ClassAttr().Matches(html).SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
        // Shell-kit classes (Btn/Icon) are defined in the shell's stylesheet, checked by its own tests.
        var phantom = used.Where(c => !c.StartsWith("btn", StringComparison.Ordinal) && !c.StartsWith("ic-", StringComparison.Ordinal) && c != "icon")
            .Where(c => !Regex.IsMatch(css, @"\." + Regex.Escape(c) + @"(?![\w-])")).ToArray();
        phantom.ShouldBeEmpty($"classes used by AboutServices but not defined in app.css: {string.Join(", ", phantom)}");
    }

    private static string AppCss()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Join(dir.FullName, "src", "AgentOs.Web", "wwwroot", "app.css");
            if (File.Exists(candidate)) { return candidate; }
        }
        throw new FileNotFoundException("app.css not found above the test output directory.");
    }

    [GeneratedRegex("class=\"([^\"]+)\"")]
    private static partial Regex ClassAttr();
}
