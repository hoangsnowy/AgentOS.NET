// Keycloak's admin REST API refused a call. The message carries Keycloak's status and raw response body — for the
// server log and for administrators' own screens, NEVER for an anonymous caller: the body can name realm internals,
// validation rules or other accounts (SignupErrors maps it to a plain product message on the public sign-up paths).
// Derives from InvalidOperationException so existing administrator-side handlers keep catching it.

using System;
using System.Net;

namespace AgentOs.Modules.Tenants.Keycloak;

/// <summary>A Keycloak admin API call failed (or Keycloak isn't configured, <see cref="StatusCode"/> null).</summary>
public sealed class KeycloakAdminException : InvalidOperationException
{
    /// <summary>Keycloak's HTTP status, when it answered.</summary>
    public HttpStatusCode? StatusCode { get; init; }

    /// <inheritdoc />
    public KeycloakAdminException() { }

    /// <inheritdoc />
    public KeycloakAdminException(string message) : base(message) { }

    /// <inheritdoc />
    public KeycloakAdminException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>A refused call: <paramref name="operation"/> answered <paramref name="status"/> with <paramref name="body"/>.</summary>
    public static KeycloakAdminException Refused(string operation, HttpStatusCode status, string? body = null) =>
        new(body is null
            ? $"Keycloak {operation} failed ({(int)status})."
            : $"Keycloak {operation} failed ({(int)status}): {body}")
        {
            StatusCode = status,
        };
}
