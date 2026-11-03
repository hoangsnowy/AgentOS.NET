// GAP-05 — every link the server hands out (an invitation, a runner's hub URL, a pairing command) is built from ONE
// configured public base URL, never from the request's Host header. A Host header is client input: built into a link
// it lets anyone who can reach the host mint an invitation or a pairing hand-off that points at their own server
// (host-header link poisoning). Outside Development the base URL is required — the host refuses to start without it.
// In Development (the standalone Web, the Aspire dev stack) an unset value falls back to the request's origin so a
// fresh clone still works with no configuration.

using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AgentOs.SharedKernel.Hosting;

/// <summary>The public origin links are built from: the configured <c>Hosting:PublicBaseUrl</c>, or — in Development
/// only — the origin of the request being served.</summary>
public sealed class PublicOrigin
{
    /// <summary>Configuration key of the public base URL (env var <c>Hosting__PublicBaseUrl</c>).</summary>
    public const string ConfigKey = "Hosting:PublicBaseUrl";

    private readonly bool _requestFallback;

    /// <summary>Create an origin. <paramref name="configured"/> must already be normalised
    /// (<see cref="Normalize"/>); <paramref name="allowRequestFallback"/> is true only in Development.</summary>
    public PublicOrigin(string? configured, bool allowRequestFallback)
    {
        Configured = string.IsNullOrWhiteSpace(configured) ? null : Normalize(configured);
        _requestFallback = allowRequestFallback;
        Host = Configured is null ? null : new Uri(Configured).Host;
    }

    /// <summary>The configured base URL (<c>scheme://host[:port][/path]</c>, no trailing slash), or null when unset.</summary>
    public string? Configured { get; }

    /// <summary>The configured base URL's host name (no port) — the default <c>AllowedHosts</c> entry. Null when unset.</summary>
    public string? Host { get; }

    /// <summary>Read <see cref="ConfigKey"/>. Throws outside Development when it is missing, and on any invalid value.</summary>
    public static PublicOrigin FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var value = configuration[ConfigKey];
        var development = environment.IsDevelopment();
        if (string.IsNullOrWhiteSpace(value) && !development)
        {
            throw new InvalidOperationException(
                $"{ConfigKey} is required outside Development: invitation, pairing and hub links are built from it, never "
                + "from the request's Host header. Set it to the desktop's public URL, e.g. Hosting__PublicBaseUrl=https://agentos.example.com "
                + "(docs/CONFIG.md).");
        }
        return new PublicOrigin(value, development);
    }

    /// <summary>Validate and normalise a base URL: absolute <c>https://</c> (plain <c>http://</c> only for a loopback
    /// host), no user info, query or fragment; the trailing slash is dropped.</summary>
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{ConfigKey} must be an absolute http(s) URL, got '{value}'.");
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new InvalidOperationException(
                $"{ConfigKey} must use https:// unless the host is loopback (got '{value}') — links carry invitation tokens and "
                + "runner credentials are fetched from it.");
        }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new InvalidOperationException($"{ConfigKey} must not carry user info, a query or a fragment (got '{value}').");
        }
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>The base URL for a link: the configured one, else (Development only) <paramref name="requestOrigin"/>.
    /// Throws when neither applies — a link is never built from an unconfigured, untrusted Host.</summary>
    public string BaseUrl(string? requestOrigin)
    {
        if (Configured is not null)
        {
            return Configured;
        }
        if (_requestFallback && !string.IsNullOrWhiteSpace(requestOrigin))
        {
            return requestOrigin.TrimEnd('/');
        }
        throw new InvalidOperationException($"{ConfigKey} is not configured, so no public link can be built.");
    }

    /// <summary>The base URL for a link served from <paramref name="request"/> (its origin is used in Development only).</summary>
    public string BaseUrl(HttpRequest? request)
        => BaseUrl(request is null ? null : $"{request.Scheme}://{request.Host}{request.PathBase}");

    /// <summary>An absolute link to <paramref name="path"/> (e.g. <c>/signup?invite=…</c>) under <see cref="BaseUrl(HttpRequest)"/>.</summary>
    public string Link(string path, HttpRequest? request)
    {
        ArgumentNullException.ThrowIfNull(path);
        return BaseUrl(request) + "/" + path.TrimStart('/');
    }
}
