// Sign-in cookie hygiene for the Web host.
//
// 1. Per-instance cookie names. Cookies are scoped to the host, NOT the port, so two AgentOS instances on one
//    host (the dev and lab stacks on localhost, or several deployments under one parent domain) would share
//    and overwrite each other's auth, OIDC correlation/nonce and antiforgery cookies. Auth:CookiePrefix
//    gives every instance its own names; the default ("agentic") keeps the historic auth cookie name.
// 2. Self-healing callback. When the OIDC callback can't be completed — the correlation/nonce cookie was lost
//    or expired (a stale tab, a cookie jar that overflowed, the IdP restarted) — the sign-in restarts ONCE
//    instead of throwing a developer exception page; a second failure shows a plain explanation + retry link.

using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Web.Auth;

internal static partial class AuthCookies
{
    public const string DefaultPrefix = "agentic";
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(5);

    /// <summary>The configured cookie prefix (lowercase letters, digits, '.', '-'; ≤ 32 chars), or the default.
    /// An invalid value fails at startup rather than producing cookies the browser silently rejects.</summary>
    public static string Prefix(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultPrefix;
        }
        var p = configured.Trim();
        return ValidPrefix().IsMatch(p)
            ? p
            : throw new InvalidOperationException(
                $"Auth:CookiePrefix '{p}' is invalid: use 1-32 lowercase letters, digits, '.' or '-', starting with a letter or digit.");
    }

    private static string RetryCookie(string prefix) => $"{prefix}.oidc.retry";

    /// <summary>A completed sign-in clears the retry marker.</summary>
    public static void ClearRetry(HttpContext http, string prefix)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Request.Cookies.ContainsKey(RetryCookie(prefix)))
        {
            http.Response.Cookies.Delete(RetryCookie(prefix), new CookieOptions { Path = "/" });
        }
    }

    /// <summary>Restart the sign-in once; on a repeated failure, show the explanation page.</summary>
    public static Task HandleRemoteFailureAsync(RemoteFailureContext ctx, string prefix)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var http = ctx.HttpContext;
        var reason = ctx.Failure?.Message ?? "unknown";
        var kind = reason.Contains("Correlation", StringComparison.OrdinalIgnoreCase) ? "expired"
            : reason.Contains("nonce", StringComparison.OrdinalIgnoreCase) ? "expired"
            : "failed";
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AgentOs.Web.Auth")
            .LogWarning("OIDC sign-in callback failed ({Kind}): {Reason}", kind, LogSafe.Scrub(reason));

        var returnUrl = SafeLocal(ctx.Properties?.RedirectUri);
        if (!http.Request.Cookies.ContainsKey(RetryCookie(prefix)))
        {
            http.Response.Cookies.Append(RetryCookie(prefix), "1", new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = RetryWindow,
            });
            http.Response.Redirect("/account/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        }
        else
        {
            http.Response.Cookies.Delete(RetryCookie(prefix), new CookieOptions { Path = "/" });
            http.Response.Redirect($"/account/signin-failed?reason={kind}&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }
        ctx.HandleResponse();
        return Task.CompletedTask;
    }

    /// <summary>Plain, self-contained explanation page (no Blazor, no external assets).</summary>
    public static IResult FailedPage(string? reason, string? returnUrl)
    {
        var why = reason == "expired"
            ? "The sign-in took too long or its cookie was lost, so it couldn't be completed."
            : "The identity provider returned an error.";
        var retry = "/account/login?returnUrl=" + Uri.EscapeDataString(SafeLocal(returnUrl));
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Couldn't sign you in — AgentOS</title>
            <style>
              :root { color-scheme: light dark; }
              body { margin: 0; min-height: 100vh; display: grid; place-items: center; font: 15px/1.5 "Inter", system-ui, sans-serif;
                     background: #f6f7f9; color: #1e1e1e; }
              @media (prefers-color-scheme: dark) { body { background: #1e1e1e; color: #eee; } .card { background: #2a2a2a; border-color: #3a3a3a; } }
              .card { max-width: 440px; margin: 16px; padding: 28px; background: #fff; border: 1px solid #d7d7db; border-radius: 12px; }
              h1 { font-size: 18px; margin: 0 0 8px; } p { margin: 0 0 12px; } ul { margin: 0 0 16px 18px; padding: 0; }
              a.btn { display: inline-block; padding: 8px 16px; border-radius: 8px; background: #3584e4; color: #fff; text-decoration: none; }
            </style></head>
            <body><main class="card">
              <h1>Couldn't sign you in</h1>
              <p>{{why}}</p>
              <ul>
                <li>Try again — this usually works.</li>
                <li>Still failing? Open AgentOS in a private window, or clear this site's cookies.</li>
              </ul>
              <a class="btn" href="{{retry}}">Try again</a>
            </main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    // Local paths only — never an absolute or protocol-relative URL (open redirect).
    private static string SafeLocal(string? url)
        => url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal)
                && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,31}$")]
    private static partial Regex ValidPrefix();
}
