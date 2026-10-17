// The HttpClient behind the About probes. Deliberately NOT from IHttpClientFactory: ServiceDefaults gives every
// factory client the standard resilience pipeline (retries + backoff), which would hide a dead service behind
// retries and blow the probe budget. One long-lived client with a pooled SocketsHttpHandler (connection
// recycling handles DNS changes) — no per-probe sockets.

using System;
using System.Net.Http;

namespace AgentOs.Web.Services.About;

/// <summary>Singleton HttpClient for service probes: short connect timeout, no retries, no redirects.</summary>
public sealed class ProbeHttpClient : IDisposable
{
    /// <summary>The shared client. Do not dispose it.</summary>
    public HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <inheritdoc />
    public void Dispose() => Client.Dispose();
}
