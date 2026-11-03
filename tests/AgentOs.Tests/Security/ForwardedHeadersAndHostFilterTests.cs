// GAP-05 — the real ServiceDefaults pipeline on a TestServer: forwarded headers are honoured only from loopback and the
// configured ingress, the Host header is checked against AllowedHosts / the public base URL (probes exempt), and a host
// outside Development refuses to start without Hosting:PublicBaseUrl.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AgentOs.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Security;

public sealed class ForwardedHeadersAndHostFilterTests
{
    private const string PublicUrl = "https://agentos.example.com";

    private static Dictionary<string, string?> Config(params (string Key, string? Value)[] extra)
    {
        var config = new Dictionary<string, string?> { ["Hosting:PublicBaseUrl"] = PublicUrl };
        foreach (var (key, value) in extra) { config[key] = value; }
        return config;
    }

    private static async Task<WebApplication> StartAsync(Dictionary<string, string?> config, string environment = "Production")
    {
        var builder = TestHosting.Builder(environment, config);
        builder.AddServiceDefaults();
        var app = builder.Build();
        app.UseAgentOsForwardedHeaders();
        app.MapGet("/echo", (HttpContext http) => $"{http.Connection.RemoteIpAddress}|{http.Request.Scheme}");
        app.MapDefaultEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<(int Status, string Body)> SendAsync(
        WebApplication app, string path, string host, string peer, params (string Name, string Value)[] headers)
    {
        var context = await app.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = path;
            c.Request.Host = new HostString(host);
            c.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            foreach (var (name, value) in headers) { c.Request.Headers[name] = value; }
        }, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    private static readonly (string, string)[] Spoofed = [("X-Forwarded-For", "198.51.100.7"), ("X-Forwarded-Proto", "https")];

    [Fact]
    public async Task ForwardedHeaders_FromAnUntrustedPeer_AreIgnored()
    {
        await using var app = await StartAsync(Config());

        var (status, body) = await SendAsync(app, "/echo", "agentos.example.com", "203.0.113.9", Spoofed);

        status.ShouldBe(200);
        body.ShouldBe("203.0.113.9|http"); // neither the client IP nor the scheme was taken from the spoofed headers
    }

    [Fact]
    public async Task ForwardedHeaders_FromLoopback_AreHonouredByDefault()
    {
        await using var app = await StartAsync(Config());

        (await SendAsync(app, "/echo", "agentos.example.com", "127.0.0.1", Spoofed)).Body.ShouldBe("198.51.100.7|https");
    }

    [Fact]
    public async Task ForwardedHeaders_FromAConfiguredIngressNetwork_AreHonoured()
    {
        await using var app = await StartAsync(Config(("Hosting:TrustedNetworks", "10.0.0.0/8; 100.64.0.0/10")));

        (await SendAsync(app, "/echo", "agentos.example.com", "10.4.5.6", Spoofed)).Body.ShouldBe("198.51.100.7|https");
        (await SendAsync(app, "/echo", "agentos.example.com", "172.20.0.4", Spoofed)).Body.ShouldBe("172.20.0.4|http");
    }

    [Fact]
    public async Task ForwardedHeaders_FromAConfiguredProxyAddress_AreHonoured()
    {
        await using var app = await StartAsync(Config(("Hosting:TrustedProxies:0", "192.0.2.10")));

        (await SendAsync(app, "/echo", "agentos.example.com", "192.0.2.10", Spoofed)).Body.ShouldBe("198.51.100.7|https");
        (await SendAsync(app, "/echo", "agentos.example.com", "192.0.2.11", Spoofed)).Body.ShouldBe("192.0.2.11|http");
    }

    [Fact]
    public async Task ForwardedHeadersEnabledEnvVar_DoesNotWidenTheTrustedPeers()
    {
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED=true makes the framework clear the allow-list; ours is a PostConfigure.
        await using var app = await StartAsync(Config(("FORWARDEDHEADERS_ENABLED", "true")));

        (await SendAsync(app, "/echo", "agentos.example.com", "203.0.113.9", Spoofed)).Body.ShouldBe("203.0.113.9|http");
    }

    [Theory]
    [InlineData("Hosting:TrustedNetworks", "10.0.0.0/33")]
    [InlineData("Hosting:TrustedNetworks", "not-a-network")]
    [InlineData("Hosting:TrustedProxies", "10.0.0.300")]
    public void InvalidTrustedEntry_FailsTheBoot(string key, string value)
    {
        var builder = TestHosting.Builder("Production", Config((key, value)));

        Should.Throw<InvalidOperationException>(() => builder.AddServiceDefaults()).Message.ShouldContain(key);
    }

    [Theory]
    [InlineData("agentos.example.com", 200)]
    [InlineData("agentos.example.com:443", 200)]
    [InlineData("localhost:8080", 200)]
    [InlineData("evil.example", 400)]
    [InlineData("agentos.example.com.evil.example", 400)]
    public async Task HostFilter_DefaultsToThePublicBaseUrlHostPlusLoopback(string host, int expected)
    {
        await using var app = await StartAsync(Config());

        (await SendAsync(app, "/echo", host, "203.0.113.9")).Status.ShouldBe(expected);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task HostFilter_LetsProbesThroughWithThePodIpAsHost(string path)
    {
        await using var app = await StartAsync(Config());

        (await SendAsync(app, path, "10.244.1.17:8080", "10.244.1.1")).Status.ShouldBe(200);
    }

    [Fact]
    public async Task HostFilter_ExplicitAllowedHosts_ReplaceTheDerivedHost()
    {
        await using var app = await StartAsync(Config(("AllowedHosts", "api.example.com;agentos-api")));

        (await SendAsync(app, "/echo", "api.example.com", "203.0.113.9")).Status.ShouldBe(200);
        (await SendAsync(app, "/echo", "agentos-api", "203.0.113.9")).Status.ShouldBe(200);
        (await SendAsync(app, "/echo", "agentos.example.com", "203.0.113.9")).Status.ShouldBe(400);
    }

    [Fact]
    public async Task HostFilter_StarAllowedHosts_StillDerivesFromThePublicBaseUrl()
    {
        await using var app = await StartAsync(Config(("AllowedHosts", "*")));

        (await SendAsync(app, "/echo", "evil.example", "203.0.113.9")).Status.ShouldBe(400);
    }

    [Fact]
    public void PublicBaseUrl_MissingOutsideDevelopment_FailsTheBoot()
    {
        var builder = TestHosting.Builder("Production");

        Should.Throw<InvalidOperationException>(() => builder.AddServiceDefaults()).Message.ShouldContain("Hosting:PublicBaseUrl");
    }

    [Fact]
    public void PublicBaseUrl_PlainHttpToARealHost_FailsTheBoot()
    {
        var builder = TestHosting.Builder("Production", new Dictionary<string, string?> { ["Hosting:PublicBaseUrl"] = "http://agentos.example.com" });

        Should.Throw<InvalidOperationException>(() => builder.AddServiceDefaults()).Message.ShouldContain("https://");
    }

    [Fact]
    public async Task Development_WithoutPublicBaseUrl_HasNoHostFilter()
    {
        await using var app = await StartAsync([], "Development");

        (await SendAsync(app, "/echo", "anything.test", "203.0.113.9")).Status.ShouldBe(200);
    }
}
