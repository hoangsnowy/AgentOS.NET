// /account/login + /account/logout (+ the dev-only /dev/view-as and the sign-in-failed page). /signin-oidc and
// /signout-callback-oidc are owned by the OIDC middleware. In dev-auto-login mode there are no Cookie/OIDC schemes, so
// login is a plain redirect (the dev user is always signed in).
//
// GAP-19 — sign-out is a POST that carries an antiforgery token. A GET sign-out let any page sign a member out with an
// <img src=".../account/logout"> (logout CSRF). The desktop submits a form App.razor renders with the token
// (agentosAccount.signOut); GET /account/logout only shows a "Sign out?" page with that form, so an old link or
// bookmark still works but never signs out by itself. A POST with a missing or stale token signs nobody out: a
// signed-in caller is sent to that confirmation page, an anonymous one to the desktop.

using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentOs.Web.Auth;

/// <summary>Maps the account routes of the Web host.</summary>
public static class AccountEndpoints
{
    /// <summary>The sign-out route (POST to sign out, GET for the confirmation page).</summary>
    public const string LogoutPath = "/account/logout";

    /// <summary>Map login / logout. <paramref name="devAutoLogin"/> = the Development fixed-user handler is active.</summary>
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app, bool devAutoLogin)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(LogoutPath, (HttpContext http, IAntiforgery antiforgery) =>
            http.User.Identity?.IsAuthenticated == true
                ? SignOutPage(antiforgery.GetAndStoreTokens(http))
                : Results.Redirect("/"))
            .AllowAnonymous();

        app.MapPost(LogoutPath, async (HttpContext http, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                return Results.Redirect(http.User.Identity?.IsAuthenticated == true ? LogoutPath : "/");
            }
            return devAutoLogin
                ? Results.Redirect("/")
                : Results.SignOut(
                    new AuthenticationProperties { RedirectUri = "/" },
                    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        })
            .AllowAnonymous()
            // Validated explicitly above (a stale token redirects instead of a bare 400).
            .DisableAntiforgery();

        if (devAutoLogin)
        {
            app.MapGet("/account/login", (string? returnUrl) => Results.Redirect(SafeLocalReturn(returnUrl)));

            // Dev-only "View as": narrow the auto-login principal's roles to preview the member (or admin)
            // desktop without standing up a second Keycloak user. Writes the cookie DevAutoAuthHandler reads,
            // then reloads so the new principal takes effect. Available only because devAutoLogin is on.
            app.MapGet("/dev/view-as", (HttpContext ctx, string? role) =>
            {
                if (AgentOs.SharedKernel.Identity.UserRoles.IsAssignable(role) || role is "member")
                {
                    ctx.Response.Cookies.Append(DevAutoAuthHandler.ViewAsCookie, role, new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = true,
                        SameSite = SameSiteMode.Lax,
                        Path = "/",
                    });
                }
                else
                {
                    ctx.Response.Cookies.Delete(DevAutoAuthHandler.ViewAsCookie);
                }
                return Results.Redirect("/");
            });
        }
        else
        {
            app.MapGet("/account/login", (string? returnUrl) =>
                Results.Challenge(
                    new AuthenticationProperties { RedirectUri = SafeLocalReturn(returnUrl) },
                    [OpenIdConnectDefaults.AuthenticationScheme]));

            // Shown only after a sign-in callback failed twice in a row (see AuthCookies.HandleRemoteFailureAsync).
            app.MapGet("/account/signin-failed", (string? reason, string? returnUrl) => AuthCookies.FailedPage(reason, returnUrl))
                .AllowAnonymous();
        }

        return app;
    }

    // Only follow a returnUrl that is a local path (starts with a single '/'), never an absolute or
    // protocol-relative URL — otherwise /account/login?returnUrl=https://evil is an open redirect.
    internal static string SafeLocalReturn(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
            && !url.StartsWith("/\\", StringComparison.Ordinal)
                ? url
                : "/";

    /// <summary>Plain, self-contained "Sign out?" page (no Blazor, no external assets) whose button POSTs the token.</summary>
    internal static IResult SignOutPage(AntiforgeryTokenSet tokens)
    {
        var field = WebUtility.HtmlEncode(tokens.FormFieldName);
        var token = WebUtility.HtmlEncode(tokens.RequestToken ?? string.Empty);
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Sign out — AgentOS.NET</title>
            <style>
              :root { color-scheme: light dark; }
              body { margin: 0; min-height: 100vh; display: grid; place-items: center; font: 15px/1.5 "Inter", system-ui, sans-serif;
                     background: #f6f7f9; color: #1e1e1e; }
              .card { max-width: 440px; margin: 16px; padding: 28px; background: #fff; border: 1px solid #d7d7db; border-radius: 12px; }
              h1 { font-size: 18px; margin: 0 0 8px; } p { margin: 0 0 16px; }
              .row { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
              button { padding: 8px 16px; border: 0; border-radius: 8px; background: #3584e4; color: #fff; font: inherit; cursor: pointer; }
              button:focus-visible, a.link:focus-visible { outline: 2px solid #3584e4; outline-offset: 2px; }
              a.link { color: #1c71d8; }
              /* After the base rules, so dark mode wins at equal specificity. */
              @media (prefers-color-scheme: dark) { body { background: #1e1e1e; color: #eee; } .card { background: #2a2a2a; border-color: #3a3a3a; } a.link { color: #99c1f1; } }
            </style></head>
            <body><main class="card">
              <h1>Sign out of AgentOS.NET?</h1>
              <p>You'll need to sign in again to use the desktop.</p>
              <form method="post" action="{{LogoutPath}}" class="row">
                <input type="hidden" name="{{field}}" value="{{token}}">
                <button type="submit" autofocus>Sign out</button>
                <a class="link" href="/">Stay signed in</a>
              </form>
            </main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
