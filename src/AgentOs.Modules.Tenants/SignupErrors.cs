// What an anonymous person sees when sign-up fails (POST /tenants/register and the desktop's /signup page). Our own
// validation messages ("Username is required.", an invitation's plain-language refusal) are written for them and pass
// through; a failure of the identity service never does — Keycloak's raw response body can name realm internals,
// password-policy details or other accounts. Those become one plain product message and the detail stays in the
// server log (the caller logs the exception).

using System;
using System.Net;
using System.Net.Http;
using AgentOs.Modules.Tenants.Keycloak;
using Microsoft.AspNetCore.Http;

namespace AgentOs.Modules.Tenants;

/// <summary>Maps a sign-up failure to the message an anonymous caller may see.</summary>
public static class SignupErrors
{
    /// <summary>Shown when the username or address is already taken (Keycloak 409).</summary>
    public const string Taken = "That username or email address is already in use. Choose another one, or sign in.";

    /// <summary>Shown when the identity service refused or failed for any other reason.</summary>
    public const string Unavailable = "Your account couldn't be created right now. Please try again in a few minutes.";

    /// <summary>Shown when the identity service could not be reached.</summary>
    public const string Unreachable = "Could not reach the identity service. Please try again.";

    /// <summary>True when <paramref name="ex"/> is a failure of the identity service (its detail is for the log only),
    /// false for the product's own validation messages.</summary>
    public static bool IsIdentityFailure(Exception ex) =>
        ex is KeycloakAdminException or KeycloakUserLeftBehindException or HttpRequestException or TimeoutException
            or OperationCanceledException;

    /// <summary>The message an anonymous caller sees for <paramref name="ex"/>.</summary>
    public static string ForPublic(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            KeycloakAdminException { StatusCode: HttpStatusCode.Conflict } => Taken,
            KeycloakAdminException or KeycloakUserLeftBehindException => Unavailable,
            HttpRequestException or TimeoutException or OperationCanceledException => Unreachable,
            _ => ex.Message,
        };
    }

    /// <summary>The HTTP status for <paramref name="ex"/> on the public sign-up endpoint.</summary>
    public static int StatusFor(Exception ex) => ex switch
    {
        KeycloakAdminException { StatusCode: HttpStatusCode.Conflict } => StatusCodes.Status409Conflict,
        _ when IsIdentityFailure(ex) => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };
}
