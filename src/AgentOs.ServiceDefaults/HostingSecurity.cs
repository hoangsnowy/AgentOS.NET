// GAP-05 — which peers may speak for the client, and which Host names this host answers to.
//
// Forwarded headers: X-Forwarded-For / X-Forwarded-Proto are honoured ONLY from a trusted proxy — loopback by default,
// plus the addresses / CIDR ranges a deployment lists in Hosting:TrustedProxies / Hosting:TrustedNetworks (the ingress
// in front of it: the azd, compose and Helm templates set theirs — docs/DEPLOYMENT.md, docs/ONPREM.md). Before, the
// allow-list was cleared, so any client could set X-Forwarded-For (a spoofed rate-limit partition and audit IP) and
// X-Forwarded-Proto. The setup is a PostConfigure, so ASPNETCORE_FORWARDEDHEADERS_ENABLED (whose own setup clears the
// lists again) can no longer widen it.
//
// Host filtering: requests whose Host header is not an allowed name get a 400. The list is AllowedHosts when set to
// anything but "*", otherwise the host of Hosting:PublicBaseUrl; loopback names are always allowed (container health
// checks, port-forwards). /health and /alive are exempt — Kubernetes / Container Apps probes send the pod IP as Host.
// With neither configured (Development) there is no filter. The framework's own host filter is neutralised so the
// probes keep working when AllowedHosts is set.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace AgentOs.ServiceDefaults;

/// <summary>Trusted-proxy and allowed-host rules shared by the Api and Web hosts.</summary>
public static class HostingSecurity
{
    /// <summary>Single proxy addresses whose forwarded headers are trusted (in addition to loopback).</summary>
    public const string TrustedProxiesKey = "Hosting:TrustedProxies";

    /// <summary>CIDR ranges whose forwarded headers are trusted (in addition to loopback).</summary>
    public const string TrustedNetworksKey = "Hosting:TrustedNetworks";

    /// <summary>The standard ASP.NET Core host-filter key; <c>*</c> or unset = derive from the public base URL.</summary>
    public const string AllowedHostsKey = "AllowedHosts";

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    private static readonly PathString[] ProbePaths = [new("/health"), new("/alive")];

    /// <summary>Apply the trusted-proxy rules to <paramref name="options"/>: X-Forwarded-For/Proto from loopback and the
    /// configured proxies / networks only. Throws on an entry that is not an IP address / CIDR range.</summary>
    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("127.0.0.0/8"));
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("::1/128"));
        foreach (var entry in List(configuration, TrustedProxiesKey))
        {
            options.KnownProxies.Add(IPAddress.TryParse(entry, out var address)
                ? address
                : throw new InvalidOperationException($"{TrustedProxiesKey}: '{entry}' is not an IP address."));
        }
        foreach (var entry in List(configuration, TrustedNetworksKey))
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(entry, out var network)
                ? network
                : throw new InvalidOperationException($"{TrustedNetworksKey}: '{entry}' is not a CIDR range (e.g. 10.0.0.0/8)."));
        }
    }

    /// <summary>The Host names this host answers to, or null when host filtering is off (no AllowedHosts and no public
    /// base URL — Development).</summary>
    public static IReadOnlyList<string>? AllowedHosts(IConfiguration configuration, PublicOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(origin);
        var configured = List(configuration, AllowedHostsKey).ToArray();
        IEnumerable<string>? hosts = configured.Length > 0 && !configured.Contains("*", StringComparer.Ordinal)
            ? configured
            : origin.Host is { } host ? [host] : null;
        return hosts is null ? null : [.. hosts.Concat(LoopbackHosts).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>True when <paramref name="request"/> may be served: a probe path, or a Host among <paramref name="allowed"/>
    /// (host names, optionally <c>*.domain</c>; the request's port is ignored).</summary>
    internal static bool IsAllowed(HttpRequest request, StringSegment[] allowed)
    {
        if (ProbePaths.Any(p => request.Path.StartsWithSegments(p)))
        {
            return true;
        }
        var host = new StringSegment(request.Headers.Host.ToString()).Trim();
        return host.Length > 0 && HostString.MatchesAny(host, allowed);
    }

    /// <summary>Middleware: reject a request whose Host is not allowed with a 400 (no body detail — it is client input).</summary>
    internal static Func<RequestDelegate, RequestDelegate> HostFilter(IReadOnlyList<string> allowed)
    {
        StringSegment[] patterns = [.. allowed.Select(a => new StringSegment(a))];
        return next => context =>
        {
            if (IsAllowed(context.Request, patterns))
            {
                return next(context);
            }
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        };
    }

    private static IReadOnlyList<string> List(IConfiguration configuration, string key) => ConfigurationLists.Read(configuration, key);
}
