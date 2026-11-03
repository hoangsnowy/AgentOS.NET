// GAP-02 (docs/security/threat-model.md §15) — one global request limiter for BOTH hosts (the Api had its own S2
// limiter; the Web had none beyond the pairing exchange). Two chained token buckets:
//   • per tenant  — one tenant's spike can't starve another (anonymous callers: per client IP instead);
//   • per user    — one person (or one API token) can't spend the whole tenant's budget.
// Limits are platform settings (ADR-0011, Settings › Platform › Request limits), read per request from a snapshot that
// is refreshed when ISettings.Version moves or every 30 s; the limit is part of the partition key, so a change takes
// effect on the next request without a restart. The built-in defaults are 10x higher in Development: the standalone
// desktop signs every browser in as ONE fixed dev user, so a per-user bucket there throttles the whole E2E suite (it did:
// ~44 tests x 2 desktop loads x 3 counted requests passed 240/min and the last tests got 429 before the dock rendered).
//
// Exempt (never throttled): endpoints carrying DisableRateLimiting metadata (health / liveness, /version, static
// assets, the Web's /_blazor/initializers + /_blazor/disconnect circuit housekeeping) and SignalR transport requests on an
// ALREADY negotiated connection (query `id=`): the Blazor circuit's websocket / long-polling traffic and the runner hub's. Opening a connection (`/negotiate`) is counted, so a flood of
// new circuits is still bounded; a transport request with a made-up id gets SignalR's cheap 404.

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentOs.ServiceDefaults;

/// <summary>Declares the request-limit settings (platform scope — a tenant must not raise its own ceiling).</summary>
/// <param name="defaults">Built-in defaults; <see cref="RequestRateLimits.Default"/> unless the host is in Development.</param>
public sealed class RequestRateLimitSettings(RequestRateLimits? defaults = null) : ISettingsContributor
{
    private readonly RequestRateLimits _defaults = defaults ?? RequestRateLimits.Default;

    /// <summary>UI category (Settings › Platform).</summary>
    public const string Category = "Platform";

    /// <summary>Requests per minute one tenant may send to one host.</summary>
    public const string TenantPerMinute = "Security:RateLimit:TenantRequestsPerMinute";

    /// <summary>Requests per minute one signed-in user (or API token) may send to one host.</summary>
    public const string UserPerMinute = "Security:RateLimit:UserRequestsPerMinute";

    /// <summary>Requests per minute one anonymous client IP may send to one host.</summary>
    public const string AnonymousPerMinute = "Security:RateLimit:AnonymousRequestsPerMinute";

    /// <summary>Built-in defaults outside Development.</summary>
    public const int DefaultTenant = 600, DefaultUser = 240, DefaultAnonymous = 120;

    /// <summary>How much higher the built-in defaults are in Development (one shared dev user, E2E suites).</summary>
    public const int DevelopmentFactor = 10;

    /// <summary>The three keys, for the Settings › Platform panel.</summary>
    public static IReadOnlyList<string> Keys { get; } = [TenantPerMinute, UserPerMinute, AnonymousPerMinute];

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return new(TenantPerMinute, SettingKind.WholeNumber, Category, "Requests per minute per tenant")
        {
            Scope = SettingScope.Platform, DefaultValue = Invariant(_defaults.Tenant), Min = 10, Max = 1_000_000,
            Help = "HTTP requests one workspace may send to each server per minute, all its users together. Over the limit the server answers 429. Health checks, static files and live desktop connections don't count.",
        };
        yield return new(UserPerMinute, SettingKind.WholeNumber, Category, "Requests per minute per user")
        {
            Scope = SettingScope.Platform, DefaultValue = Invariant(_defaults.User), Min = 10, Max = 1_000_000,
            Help = "HTTP requests one signed-in person or API token may send to each server per minute.",
        };
        yield return new(AnonymousPerMinute, SettingKind.WholeNumber, Category, "Requests per minute per anonymous client")
        {
            Scope = SettingScope.Platform, DefaultValue = Invariant(_defaults.Anonymous), Min = 5, Max = 1_000_000,
            Help = "Requests per minute from one address that isn't signed in — the sign-in, sign-up and invitation pages.",
        };
    }

    private static string Invariant(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The effective per-minute limits.</summary>
public readonly record struct RequestRateLimits(int Tenant, int User, int Anonymous)
{
    /// <summary>The built-in defaults (every environment but Development).</summary>
    public static RequestRateLimits Default { get; } = new(
        RequestRateLimitSettings.DefaultTenant, RequestRateLimitSettings.DefaultUser, RequestRateLimitSettings.DefaultAnonymous);

    /// <summary>The Development defaults: <see cref="Default"/> times <see cref="RequestRateLimitSettings.DevelopmentFactor"/>.</summary>
    public static RequestRateLimits Development { get; } = new(
        RequestRateLimitSettings.DefaultTenant * RequestRateLimitSettings.DevelopmentFactor,
        RequestRateLimitSettings.DefaultUser * RequestRateLimitSettings.DevelopmentFactor,
        RequestRateLimitSettings.DefaultAnonymous * RequestRateLimitSettings.DevelopmentFactor);

    /// <summary>The defaults for a host (<paramref name="development"/> = <c>IHostEnvironment.IsDevelopment()</c>).</summary>
    public static RequestRateLimits For(bool development) => development ? Development : Default;
}

/// <summary>Builds the global limiter. Singleton.</summary>
public sealed class RequestRateLimitPolicy
{
    /// <summary>Meter for rejected requests (<c>agentos.http.rate_limited</c>).</summary>
    public const string MeterName = "AgentOs.Http";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>(
        "agentos.http.rate_limited", unit: "{request}", description: "Requests refused with 429 by the global request limiter.");

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    // Six refills a minute: a full minute's budget is available as a burst, then it refills smoothly.
    private const int RefillsPerMinute = 6;

    private readonly ISettings? _settings;
    private readonly TimeProvider _clock;
    private readonly RequestRateLimits _defaults;
    private readonly Lock _gate = new();
    private Snapshot _snapshot;

    private sealed record Snapshot(RequestRateLimits Limits, long Version, long ReadAt);

    /// <summary>Creates the policy; with no <paramref name="settings"/> (or an unreadable value) <paramref name="defaults"/> apply.</summary>
    public RequestRateLimitPolicy(ISettings? settings = null, TimeProvider? clock = null, RequestRateLimits? defaults = null)
    {
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _defaults = defaults ?? RequestRateLimits.Default;
        _snapshot = new Snapshot(_defaults, long.MinValue, long.MinValue);
    }

    /// <summary>The limits in effect now (cached; re-read on a settings write or every 30 s).</summary>
    public RequestRateLimits Current
    {
        get
        {
            var snap = Volatile.Read(ref _snapshot);
            if (_settings is null)
            {
                return snap.Limits;
            }
            var now = _clock.GetTimestamp();
            if (snap.Version == _settings.Version && snap.ReadAt != long.MinValue && _clock.GetElapsedTime(snap.ReadAt, now) < RefreshEvery)
            {
                return snap.Limits;
            }
            lock (_gate)
            {
                var version = _settings.Version;
                var limits = new RequestRateLimits(
                    Read(RequestRateLimitSettings.TenantPerMinute, _defaults.Tenant),
                    Read(RequestRateLimitSettings.UserPerMinute, _defaults.User),
                    Read(RequestRateLimitSettings.AnonymousPerMinute, _defaults.Anonymous));
                Volatile.Write(ref _snapshot, new Snapshot(limits, version, now));
                return limits;
            }
        }
    }

    private int Read(string key, int fallback)
    {
        try
        {
            return int.TryParse(_settings!.Read(key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A settings-store hiccup must not take every request down with it: keep serving with the built-in limit.
            _ = ex.Message;
            return fallback;
        }
    }

    /// <summary>True for requests the limiter never counts (see the file header).</summary>
    public static bool IsExempt(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint();
        return endpoint?.Metadata.GetMetadata<HubMetadata>() is not null && context.Request.Query.ContainsKey("id");
    }

    /// <summary>The per-tenant (anonymous: per-IP) partition key, or null when exempt.</summary>
    internal static string? TenantKey(HttpContext context, RequestRateLimits limits)
    {
        if (IsExempt(context))
        {
            return null;
        }
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var tenant = user.GetTenantId() ?? "default";
            return string.Create(CultureInfo.InvariantCulture, $"t|{limits.Tenant}|{tenant}");
        }
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return string.Create(CultureInfo.InvariantCulture, $"a|{limits.Anonymous}|{ip}");
    }

    /// <summary>The per-user partition key, or null when exempt or anonymous.</summary>
    internal static string? UserKey(HttpContext context, RequestRateLimits limits)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true || IsExempt(context))
        {
            return null;
        }
        var id = user.GetUserId() ?? user.Identity.Name ?? "unknown";
        var tenant = user.GetTenantId() ?? "default";
        return string.Create(CultureInfo.InvariantCulture, $"u|{limits.User}|{tenant}|{id}");
    }

    /// <summary>The permits a key's bucket holds — the number between the first two <c>|</c>.</summary>
    internal static int LimitOf(string key)
    {
        var rest = key.AsSpan(2);
        var bar = rest.IndexOf('|');
        return int.Parse(rest[..bar], NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    internal static TokenBucketRateLimiterOptions Bucket(int perMinute) => new()
    {
        TokenLimit = perMinute,
        TokensPerPeriod = Math.Max(1, (int)Math.Ceiling(perMinute / (double)RefillsPerMinute)),
        ReplenishmentPeriod = TimeSpan.FromMinutes(1.0 / RefillsPerMinute),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    /// <summary>The chained (tenant → user) global limiter.</summary>
    public PartitionedRateLimiter<HttpContext> CreateLimiter()
    {
        var tenant = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            TenantKey(http, Current) is { } key
                ? RateLimitPartition.GetTokenBucketLimiter(key, static k => Bucket(LimitOf(k)))
                : RateLimitPartition.GetNoLimiter("exempt"));
        var user = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            UserKey(http, Current) is { } key
                ? RateLimitPartition.GetTokenBucketLimiter(key, static k => Bucket(LimitOf(k)))
                : RateLimitPartition.GetNoLimiter("exempt"));
        return PartitionedRateLimiter.CreateChained(tenant, user);
    }

    /// <summary>The 429 answer: a Retry-After hint and a metric.</summary>
    internal static ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var http = context.HttpContext;
        Rejected.Add(1, new KeyValuePair<string, object?>("authenticated", http.User.Identity?.IsAuthenticated == true));
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromMinutes(1.0 / RefillsPerMinute);
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(Math.Max(1, retryAfter.TotalSeconds))).ToString(CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Wiring for the global request limiter.</summary>
public static class RequestRateLimitingExtensions
{
    /// <summary>
    /// Registers the global per-tenant + per-user limiter (and its settings). A host may still add named policies with
    /// its own <c>AddRateLimiter</c> call (they chain with the global one). Use with <c>app.UseRateLimiter()</c> AFTER
    /// <c>UseAuthentication</c> — the partitions read the tenant + user claims.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="development">The host runs in Development: the built-in defaults are <see cref="RequestRateLimits.Development"/>.</param>
    public static IServiceCollection AddAgentOsRequestRateLimiting(this IServiceCollection services, bool development = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        var defaults = RequestRateLimits.For(development);
        if (!services.Any(d => d.ServiceType == typeof(RequestRateLimitPolicy)))
        {
            services.AddSingleton<ISettingsContributor>(new RequestRateLimitSettings(defaults));
        }
        services.TryAddSingleton(sp => new RequestRateLimitPolicy(sp.GetService<ISettings>(), sp.GetService<TimeProvider>(), defaults));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = RequestRateLimitPolicy.OnRejectedAsync;
        });
        services.AddOptions<RateLimiterOptions>()
            .Configure<RequestRateLimitPolicy>((o, policy) => o.GlobalLimiter = policy.CreateLimiter());
        return services;
    }
}
