// Production error surface for the Web host. UseExceptionHandler re-executes the failed request
// through this terminal handler — a Razor component route would be the natural fit, but the shell
// renders globally interactive (App.razor mounts <Routes> with InteractiveServer, prerender:false)
// and AuthorizeRouteView would bounce an anonymous error hit to /login, so the crash screen is
// served as static HTML with no routing, no auth, and no circuit. Styled from app.css tokens with
// inline fallbacks in case static assets are also failing.

using System.Diagnostics;

namespace AgentOs.Web;

internal static class ErrorPage
{
    public static Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var requestId = System.Net.WebUtility.HtmlEncode(Activity.Current?.Id ?? context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        // API-ish callers (fetch/SSE probes) get RFC 7807 JSON; browsers get the branded page.
        var accept = context.Request.Headers.Accept.ToString();
        if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "application/problem+json";
            return context.Response.WriteAsync(
                $$"""{"type":"about:blank","title":"An unexpected error occurred.","status":500,"traceId":"{{requestId}}"}""");
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>AgentOS.NET — something went wrong</title>
                <link rel="stylesheet" href="/app.css" />
                <style>
                    .error-shell { min-height: 100vh; display: flex; align-items: center; justify-content: center;
                                   background: var(--bg, #11141a); color: var(--txt, #e8eaef);
                                   font-family: var(--font, Inter, system-ui, sans-serif); }
                    .error-card  { background: var(--bg-2, #1a1e26); border: 1px solid var(--line, #2a2f3a);
                                   border-radius: var(--r-4, 6px); padding: var(--space-7, 36px);
                                   max-width: 440px; text-align: center; }
                    .error-brand { font-weight: 700; letter-spacing: 0.02em; margin-bottom: var(--space-4, 16px); }
                    .error-title { font-size: var(--fs-lg, 17px); font-weight: 600; margin: 0 0 var(--space-2, 8px); }
                    .error-sub   { color: var(--txt-soft, #aab1bd); font-size: var(--fs-sm, 13px); margin: 0 0 var(--space-5, 24px); }
                    .error-id    { color: var(--txt-dim, #7c8494); font-family: var(--mono, monospace);
                                   font-size: var(--fs-xs, 11px); margin-top: var(--space-5, 24px); }
                    .error-btn   { display: inline-block; background: var(--accent, #3daee9); color: var(--txt-on-accent, #fff);
                                   border-radius: var(--r-3, 5px); padding: 8px 16px; text-decoration: none;
                                   font-size: var(--fs-sm, 13px); font-weight: 500; }
                </style>
            </head>
            <body>
                <div class="error-shell">
                    <div class="error-card">
                        <div class="error-brand">AgentOS.NET</div>
                        <h1 class="error-title">Something went wrong</h1>
                        <p class="error-sub">The request hit an unexpected error. It has been logged on the server —
                           retry, and if it keeps happening give your administrator the request id below.</p>
                        <a class="error-btn" href="/">Back to the desktop</a>
                        <div class="error-id">request id: {{requestId}}</div>
                    </div>
                </div>
            </body>
            </html>
            """);
    }
}
