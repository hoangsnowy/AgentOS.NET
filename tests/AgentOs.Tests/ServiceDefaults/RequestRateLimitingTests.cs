// GAP-02 — the global request limiter shared by the Api and the Web (ServiceDefaults). Unit level: partitions (tenant,
// user, anonymous IP), exemptions, the settings snapshot, and a minimal TestServer host proving the 429 + Retry-After.
// The real hosts (Blazor circuit traffic, static assets, health) are covered by HostSecurityTests.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.ServiceDefaults;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.ServiceDefaults;

public sealed class RequestRateLimitingTests
{
    private static readonly RequestRateLimits Limits = new(Tenant: 600, User: 240, Anonymous: 120);

    private static DefaultHttpContext Anonymous(string ip = "203.0.113.7")
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return http;
    }

    private static DefaultHttpContext SignedIn(string tenant, string user) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimNames.Tenant, tenant), new Claim(ClaimNames.Subject, user)], "test")),
    };

    [Fact]
    public void TenantKey_SignedIn_PartitionsByTenantWithTheLimitInTheKey()
    {
        RequestRateLimitPolicy.TenantKey(SignedIn("acme", "u1"), Limits).ShouldBe("t|600|acme");
        RequestRateLimitPolicy.TenantKey(SignedIn("acme", "u2"), Limits).ShouldBe("t|600|acme");
        RequestRateLimitPolicy.TenantKey(SignedIn("globex", "u1"), Limits).ShouldBe("t|600|globex");
    }

    [Fact]
    public void TenantKey_Anonymous_PartitionsByClientIp()
    {
        RequestRateLimitPolicy.TenantKey(Anonymous("203.0.113.7"), Limits).ShouldBe("a|120|203.0.113.7");
        RequestRateLimitPolicy.TenantKey(Anonymous("198.51.100.1"), Limits).ShouldBe("a|120|198.51.100.1");
    }

    [Fact]
    public void UserKey_SignedIn_IsPerTenantAndUser_AnonymousHasNone()
    {
        RequestRateLimitPolicy.UserKey(SignedIn("acme", "u1"), Limits).ShouldBe("u|240|acme|u1");
        RequestRateLimitPolicy.UserKey(Anonymous(), Limits).ShouldBeNull();
    }

    [Theory]
    [InlineData("t|600|acme", 600)]
    [InlineData("a|5|::1", 5)]
    [InlineData("u|240|acme|user|with|bars", 240)]
    public void LimitOf_ReadsThePermitCountBackFromTheKey(string key, int expected) =>
        RequestRateLimitPolicy.LimitOf(key).ShouldBe(expected);

    [Fact]
    public void Bucket_HoldsAMinuteAndRefillsSixTimesAMinute()
    {
        var bucket = RequestRateLimitPolicy.Bucket(120);
        bucket.TokenLimit.ShouldBe(120);
        bucket.TokensPerPeriod.ShouldBe(20);
        bucket.ReplenishmentPeriod.ShouldBe(TimeSpan.FromSeconds(10));
        bucket.QueueLimit.ShouldBe(0);
        RequestRateLimitPolicy.Bucket(5).TokensPerPeriod.ShouldBe(1);
    }

    [Fact]
    public void IsExempt_SignalRTransportOnAnOpenConnection_ButNotNegotiate()
    {
        var hub = new Endpoint(null, new EndpointMetadataCollection(new HubMetadata(typeof(Hub))), "hub");
        var transport = Anonymous();
        transport.SetEndpoint(hub);
        transport.Request.QueryString = new QueryString("?id=abc");
        var negotiate = Anonymous();
        negotiate.SetEndpoint(hub);
        negotiate.Request.QueryString = new QueryString("?negotiateVersion=1");
        var plain = Anonymous();
        plain.Request.QueryString = new QueryString("?id=abc"); // an ordinary route with ?id= is still counted

        RequestRateLimitPolicy.IsExempt(transport).ShouldBeTrue();
        RequestRateLimitPolicy.TenantKey(transport, Limits).ShouldBeNull();
        RequestRateLimitPolicy.IsExempt(negotiate).ShouldBeFalse();
        RequestRateLimitPolicy.IsExempt(plain).ShouldBeFalse();
    }

    [Fact]
    public void Current_ReadsSettings_AndRefreshesWhenTheVersionMoves()
    {
        var settings = new MemorySettings { [RequestRateLimitSettings.AnonymousPerMinute] = "7" };
        var clock = new ManualClock();
        var policy = new RequestRateLimitPolicy(settings, clock);

        policy.Current.ShouldBe(new RequestRateLimits(600, 240, 7));

        settings[RequestRateLimitSettings.TenantPerMinute] = "1000"; // a write bumps Version
        policy.Current.Tenant.ShouldBe(1000);
    }

    [Fact]
    public void Current_WriteOnAnotherHost_IsPickedUpWithin30Seconds()
    {
        var settings = new MemorySettings { [RequestRateLimitSettings.UserPerMinute] = "50" };
        var clock = new ManualClock();
        var policy = new RequestRateLimitPolicy(settings, clock);
        policy.Current.User.ShouldBe(50);

        settings.SetWithoutVersionBump(RequestRateLimitSettings.UserPerMinute, "60");
        policy.Current.User.ShouldBe(50);
        clock.Advance(TimeSpan.FromSeconds(31));
        policy.Current.User.ShouldBe(60);
    }

    [Fact]
    public void Current_StoreFails_KeepsServingWithTheDefaults()
    {
        var policy = new RequestRateLimitPolicy(new MemorySettings { Throw = true }, new ManualClock());
        policy.Current.ShouldBe(RequestRateLimits.Default);
    }

    [Fact]
    public void Settings_ArePlatformScoped_WithValidDefaults()
    {
        var descriptors = new RequestRateLimitSettings().Describe().ToArray();
        descriptors.Select(d => d.Key).ShouldBe(RequestRateLimitSettings.Keys);
        descriptors.ShouldAllBe(d => d.Scope == SettingScope.Platform && d.Kind == SettingKind.WholeNumber);
        descriptors.ShouldAllBe(d => d.Category == "Platform");
        // The catalog validates every default against its own range at startup.
        _ = new AgentOs.Modules.AppConfig.Settings.SettingsCatalog([new RequestRateLimitSettings()]);
    }

    [Fact]
    public void Defaults_Development_AreTenTimesHigher_InTheSettingsAndThePolicy()
    {
        RequestRateLimits.For(development: false).ShouldBe(RequestRateLimits.Default);
        RequestRateLimits.For(development: true).ShouldBe(new RequestRateLimits(6000, 2400, 1200));

        var descriptors = new RequestRateLimitSettings(RequestRateLimits.Development).Describe().ToDictionary(d => d.Key, d => d.DefaultValue);
        descriptors[RequestRateLimitSettings.UserPerMinute].ShouldBe("2400");
        _ = new AgentOs.Modules.AppConfig.Settings.SettingsCatalog([new RequestRateLimitSettings(RequestRateLimits.Development)]);

        // An unreadable store falls back to the host's own defaults, not the production ones.
        new RequestRateLimitPolicy(new MemorySettings { Throw = true }, new ManualClock(), RequestRateLimits.Development)
            .Current.ShouldBe(RequestRateLimits.Development);
    }

    [Fact]
    public void AddAgentOsRequestRateLimiting_CalledTwice_DeclaresTheSettingsOnce()
    {
        var services = new ServiceCollection();
        services.AddAgentOsRequestRateLimiting(development: true);
        services.AddAgentOsRequestRateLimiting(development: true);

        services.Count(d => d.ServiceType == typeof(ISettingsContributor)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(RequestRateLimitPolicy)).ShouldBe(1);
    }

    // ── A minimal host: the limiter end to end ──

    private static async Task<(WebApplication App, HttpClient Client)> HostAsync(MemorySettings settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISettings>(settings);
        builder.Services.AddAgentOsRequestRateLimiting();
        var app = builder.Build();
        app.Use((ctx, next) =>
        {
            // Stand-in for authentication: X-Tenant / X-User headers sign the request in.
            if (ctx.Request.Headers.TryGetValue("X-Tenant", out var tenant))
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimNames.Tenant, tenant.ToString()), new Claim(ClaimNames.Subject, ctx.Request.Headers["X-User"].ToString())], "test"));
            }
            return next(ctx);
        });
        app.UseRateLimiter();
        app.MapGet("/work", () => "ok");
        app.MapGet("/probe", () => "ok").DisableRateLimiting();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, app.GetTestClient());
    }

    private static async Task<HttpStatusCode[]> SendAsync(HttpClient client, string path, int count, string? tenant = null, string? user = null)
    {
        var codes = new HttpStatusCode[count];
        for (var i = 0; i < count; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (tenant is not null)
            {
                request.Headers.Add("X-Tenant", tenant);
                request.Headers.Add("X-User", user ?? "someone");
            }
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            codes[i] = response.StatusCode;
        }
        return codes;
    }

    [Fact]
    public async Task Host_AnonymousPastTheLimit_Gets429WithRetryAfter()
    {
        var (app, client) = await HostAsync(new MemorySettings { [RequestRateLimitSettings.AnonymousPerMinute] = "5" });
        await using (app)
        using (client)
        {
            (await SendAsync(client, "/work", 5)).ShouldAllBe(c => c == HttpStatusCode.OK);

            using var refused = await client.GetAsync(new Uri("/work", UriKind.Relative), TestContext.Current.CancellationToken);
            refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            refused.Headers.RetryAfter.ShouldNotBeNull();
            refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeGreaterThanOrEqualTo(1);
        }
    }

    [Fact]
    public async Task Host_DisableRateLimitingEndpoint_IsNeverThrottled()
    {
        var (app, client) = await HostAsync(new MemorySettings { [RequestRateLimitSettings.AnonymousPerMinute] = "5" });
        await using (app)
        using (client)
        {
            (await SendAsync(client, "/probe", 20)).ShouldAllBe(c => c == HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Host_OneUserPastTheirLimit_DoesNotStopTheirColleague()
    {
        var (app, client) = await HostAsync(new MemorySettings
        {
            [RequestRateLimitSettings.UserPerMinute] = "10",
            [RequestRateLimitSettings.TenantPerMinute] = "100",
        });
        await using (app)
        using (client)
        {
            (await SendAsync(client, "/work", 10, "acme", "alice")).ShouldAllBe(c => c == HttpStatusCode.OK);
            (await SendAsync(client, "/work", 1, "acme", "alice")).ShouldBe([HttpStatusCode.TooManyRequests]);
            (await SendAsync(client, "/work", 1, "acme", "bob")).ShouldBe([HttpStatusCode.OK]);
        }
    }

    [Fact]
    public async Task Host_OneTenantPastItsLimit_DoesNotStopAnotherTenant()
    {
        var (app, client) = await HostAsync(new MemorySettings
        {
            [RequestRateLimitSettings.UserPerMinute] = "100",
            [RequestRateLimitSettings.TenantPerMinute] = "10",
        });
        await using (app)
        using (client)
        {
            for (var i = 0; i < 10; i++)
            {
                (await SendAsync(client, "/work", 1, "acme", $"user{i}")).ShouldBe([HttpStatusCode.OK]);
            }
            (await SendAsync(client, "/work", 1, "acme", "fresh")).ShouldBe([HttpStatusCode.TooManyRequests]);
            (await SendAsync(client, "/work", 1, "globex", "fresh")).ShouldBe([HttpStatusCode.OK]);
        }
    }

    /// <summary>A clock that only moves when told to.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>An in-memory <see cref="ISettings"/> (platform values only).</summary>
    internal sealed class MemorySettings : ISettings
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private long _version;

        public bool Throw { get; init; }

        public long Version => Interlocked.Read(ref _version);

        public string this[string key]
        {
            set
            {
                _values[key] = value;
                Interlocked.Increment(ref _version);
            }
        }

        public void SetWithoutVersionBump(string key, string value) => _values[key] = value;

        public ValueTask<SettingValue> GetAsync(string key, string? tenantId, CancellationToken cancellationToken = default) =>
            Throw
                ? throw new InvalidOperationException("store down")
                : ValueTask.FromResult(_values.TryGetValue(key, out var v) ? new SettingValue(v, SettingSource.Platform) : default);

        public ValueTask<IReadOnlyList<SettingView>> ListAsync(string? tenantId, string? category = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SettingView>>([]);

        public ValueTask<SettingWriteResult> SetAsync(string key, string? value, SettingScope scope, string? tenantId, string actor, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SettingWriteResult.Success);
    }
}
