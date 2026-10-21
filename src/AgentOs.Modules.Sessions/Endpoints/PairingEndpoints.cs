// VS Code (and other editors) browser-pairing flow — the "sign in" experience that replaces the
// copy-the-token-into-env dance. The editor extension opens /pair/vscode in the browser; the member
// (already logged in via Keycloak) approves; we provision a runner and hand the editor a ONE-TIME code
// via its vscode:// callback. The extension then exchanges that code (a direct POST, token never in a
// URL) for the runner credentials. See tools/agentos-vscode.

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Sessions.Pairing;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace AgentOs.Modules.Sessions.Endpoints;

// Public so the Web host maps these directly (app.MapPairingEndpoints): they belong on the Web, not the
// API — the approve page authenticates via the browser's OIDC cookie, and the extension + browser both
// target the Web origin.
public static class PairingEndpoints
{
    // Only editor deep-link schemes may be redirect targets — blocks open-redirect to arbitrary schemes.
    private static readonly string[] AllowedCallbackSchemes =
        ["vscode://", "vscode-insiders://", "vscodium://", "cursor://", "windsurf://"];

    /// <summary>Rate-limiter policy name the host must register for the code exchange.</summary>
    public const string ExchangeRateLimitPolicy = "pairing-exchange";

    public static void MapPairingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/pair/vscode", ApprovePage).RequireAuthorization("Member");
        endpoints.MapPost("/pair/vscode", ApproveAsync).RequireAuthorization("Member").DisableAntiforgery();
        // Anonymous — gated by the one-time code. A typed code is short (≈39 bits), so the host rate-limits this
        // route per client under ExchangeRateLimitPolicy (the Web registers it).
        endpoints.MapPost("/runner/pair/exchange", ExchangeAsync).RequireRateLimiting(ExchangeRateLimitPolicy);
    }

    // The branded approve page. Self-contained HTML so it renders without the Blazor shell.
    private static IResult ApprovePage(string? callback, string? state, string? label, ITenantContext tenant)
    {
        if (!IsAllowedCallback(callback))
        {
            return Results.Content(Page("Invalid pairing request",
                "<p>This link is missing a valid editor callback. Start pairing from the editor extension "
                + "(<strong>AgentOS.NET: Connect</strong>), not by opening this URL directly.</p>"), "text/html");
        }

        var who = WebUtility.HtmlEncode(tenant.UserName ?? tenant.UserId ?? "you");
        var machine = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(label) ? "My dev machine" : label);
        var body =
            $"<p>Pair a new AgentOS.NET runner to your editor, signed in as <strong>{who}</strong>. "
            + "The runner executes AI coding sessions on this machine.</p>"
            + "<form method=\"post\" action=\"/pair/vscode\">"
            + $"<input type=\"hidden\" name=\"callback\" value=\"{WebUtility.HtmlEncode(callback)}\" />"
            + $"<input type=\"hidden\" name=\"state\" value=\"{WebUtility.HtmlEncode(state ?? string.Empty)}\" />"
            + "<label>Machine name</label>"
            + $"<input class=\"f\" type=\"text\" name=\"label\" value=\"{machine}\" maxlength=\"200\" />"
            + "<button class=\"b\" type=\"submit\">Approve &amp; pair</button>"
            + "</form>";
        return Results.Content(Page("Pair this machine", body), "text/html");
    }

    private static async Task<IResult> ApproveAsync(
        [FromForm] string callback,
        [FromForm] string? state,
        [FromForm] string? label,
        HttpContext http,
        ITenantContext tenant,
        IRunnerProvisioningService provisioning,
        IPairingCodeStore codes,
        CancellationToken ct)
    {
        if (!IsAllowedCallback(callback))
        {
            return Results.BadRequest("Invalid callback.");
        }

        var provisioned = await provisioning
            .ProvisionAsync(tenant.TenantId, tenant.UserId ?? string.Empty,
                string.IsNullOrWhiteSpace(label) ? "My dev machine" : label, ct)
            .ConfigureAwait(false);

        var hubUrl = $"{http.Request.Scheme}://{http.Request.Host}/hubs/remote-agent";
        var code = codes.Stash(new PairingPayload(provisioned.RunnerId, provisioned.Token, hubUrl));

        // Hand the editor only the one-time code; it exchanges it for the token over a direct POST.
        var redirect = $"{callback}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state ?? string.Empty)}";
        return Results.Content(
            Page("Pairing approved",
                "<p>Approved. Return to your editor — it should connect automatically. "
                + "You can close this tab.</p>"
                + $"<a class=\"b\" href=\"{WebUtility.HtmlEncode(redirect)}\">Open editor</a>"
                + $"<script>location.href={System.Text.Json.JsonSerializer.Serialize(redirect)};</script>"),
            "text/html");
    }

    private static IResult ExchangeAsync(ExchangeRequest request, IPairingCodeStore codes)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.BadRequest("code is required.");
        }
        var payload = codes.Redeem(request.Code);
        return payload is null
            ? Results.NotFound("Pairing code is invalid, already used, or expired.")
            : Results.Ok(new PairExchangeDto(payload.RunnerId, payload.Token, payload.HubUrl));
    }

    private static bool IsAllowedCallback(string? callback)
    {
        if (string.IsNullOrWhiteSpace(callback))
        {
            return false;
        }
        return AllowedCallbackSchemes.Any(
            scheme => callback.StartsWith(scheme, StringComparison.OrdinalIgnoreCase));
    }

    // Minimal branded page chrome (AgentOS.NET, dark) — no external CSS dependency.
    private static string Page(string title, string body) =>
        "<!doctype html><html><head><meta charset=\"utf-8\" />"
        + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />"
        + $"<title>{WebUtility.HtmlEncode(title)} · AgentOS.NET</title><style>"
        + ":root{color-scheme:dark}*{box-sizing:border-box}"
        + "body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0f1117;"
        + "color:#e6e8ee;font:15px/1.5 system-ui,-apple-system,Segoe UI,sans-serif}"
        + ".c{width:min(420px,92vw);background:#171a23;border:1px solid #262a36;border-radius:12px;"
        + "padding:28px 30px;box-shadow:0 12px 40px rgba(0,0,0,.45)}"
        + ".m{font-weight:700;letter-spacing:.3px;color:#9aa4b2;font-size:13px;margin-bottom:14px}"
        + "h1{font-size:20px;margin:0 0 12px}p{color:#c2c8d2;margin:0 0 16px}"
        + "label{display:block;font-size:12px;color:#9aa4b2;margin:14px 0 6px}"
        + ".f{width:100%;padding:9px 11px;background:#0f1117;border:1px solid #2c313f;border-radius:8px;"
        + "color:#e6e8ee;font-size:14px}"
        + ".b{display:inline-block;margin-top:18px;padding:10px 16px;background:#4c6ef5;color:#fff;"
        + "border:0;border-radius:8px;font-size:14px;font-weight:600;cursor:pointer;text-decoration:none}"
        + ".b:hover{background:#4263e0}</style></head><body><div class=\"c\">"
        + "<div class=\"m\">AgentOS.NET</div>" + $"<h1>{WebUtility.HtmlEncode(title)}</h1>{body}</div></body></html>";
}

/// <summary>Body for <c>POST /runner/pair/exchange</c>.</summary>
internal sealed record ExchangeRequest(string Code);

/// <summary>Response for <c>POST /runner/pair/exchange</c> — the runner's connect credentials.</summary>
internal sealed record PairExchangeDto(Guid RunnerId, string Token, string HubUrl);
