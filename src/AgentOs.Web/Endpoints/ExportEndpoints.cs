// Same-origin, cookie-authed browser downloads for the admin desktop apps (Cost, Settings → Data, Settings → Audit log).
// Mapped on the Web (not via the Api's MapModuleEndpoints) so a circuit can trigger the download like /runner/download.
//
// GAP-17: the tenant comes from the OIDC `tenant` claim and nothing else — a signed-in principal without one is a
// misconfigured token and gets a 403, never the shared "default" tenant (the old `?? "default"` fallbacks exported the
// default tenant's cost, data and audit trail to such a caller).

using System;
using System.Threading;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Data;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace AgentOs.Web.Endpoints;

/// <summary>Maps <c>/cost/export</c>, <c>/tenant/export</c> and <c>/audit/export</c>.</summary>
public static class ExportEndpoints
{
    /// <summary>Map the export routes (RBAC v2: cost + tenant export need ExportData, the audit CSV ViewAudit — admin or
    /// auditor; each also needs a tenant claim).</summary>
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Cost CSV export for the admin Cost app; `days` (0/absent = all time) sets the cutoff.
        app.MapGet("/cost/export", async (HttpContext http, IPipelineRunRepository runs, int? days) =>
        {
            if (!http.User.Can(Permission.ExportData) || http.User.GetTenantId() is not { } tenant)
            {
                return Results.Forbid();
            }
            DateTimeOffset? since = days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : null;
            var summary = await runs.GetCostSummaryForTenantAsync(tenant, since);
            var bytes = System.Text.Encoding.UTF8.GetBytes(AgentOs.Modules.Pipeline.Cost.CostCsv.ToCsv(summary));
            return Results.File(bytes, "text/csv", "agentos-cost.csv");
        }).RequirePermission(Permission.ExportData);

        // Tenant data export (Settings → Data, E1.4): one JSON document of every row the tenant owns, secrets excluded,
        // streamed. A tenant admin exports their own tenant; ?tenant= another one needs platform-admin. Audited only once
        // the export has been written — a refused export (a secret column it can't safely redact) is a 409.
        app.MapGet("/tenant/export", async (HttpContext http, ITenantDataService data, IAuditLog audit, ILoggerFactory loggers, string? tenant) =>
        {
            if (!http.User.Can(Permission.ExportData) || http.User.GetTenantId() is not { } own) { return Results.Forbid(); }
            var target = string.IsNullOrWhiteSpace(tenant) ? own : tenant;
            if (!string.Equals(target, own, StringComparison.Ordinal) && !http.User.IsPlatformAdmin()) { return Results.Forbid(); }
            if (!data.IsAvailable) { return Results.NotFound("No database in this run — nothing to export."); }
            http.Response.ContentType = "application/json";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"agentos-{target}-{DateTime.UtcNow:yyyyMMdd}.json\"";
            try
            {
                await data.ExportAsync(target, http.Response.Body, http.RequestAborted);
            }
            catch (TenantExportRefusedException ex) when (!http.Response.HasStarted)
            {
                // Fail closed: the redaction check runs before the first byte, so nothing was sent. Detail stays in the log.
                loggers.CreateLogger("TenantExport").LogWarning(ex, "Tenant export refused");
                http.Response.Headers.ContentDisposition = default;
                return Results.Problem(
                    title: "Export stopped",
                    detail: "The export was stopped before anything was written: a table that holds secrets has changed and can't be "
                        + "safely redacted yet. Nothing was exported. Ask your operator to update AgentOS.NET, then try again.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            await audit.WriteAsync(new AuditEntry(Guid.NewGuid(), target, http.User.Identity?.Name,
                AuditActions.TenantExported, $"export of {target}", null, null, DateTimeOffset.UtcNow), CancellationToken.None);
            return Results.Empty;
        }).RequirePermission(Permission.ExportData);

        // Audit CSV export (Settings → Audit log). A tenant admin exports their own tenant; scope=platform (platform
        // settings changes) needs platform-admin.
        app.MapGet("/audit/export", async (HttpContext http, IAuditLog audit, string? scope) =>
        {
            var platform = string.Equals(scope, "platform", StringComparison.Ordinal);
            if (!http.User.Can(Permission.ViewAudit) || (platform && !http.User.IsPlatformAdmin()))
            {
                return Results.Forbid();
            }
            var tenant = platform ? AuditScopes.Platform : http.User.GetTenantId();
            if (tenant is null)
            {
                return Results.Forbid();
            }
            var rows = await audit.ListAsync(tenant, 10_000);
            var bytes = System.Text.Encoding.UTF8.GetBytes(AuditCsv.ToCsv(rows));
            return Results.File(bytes, "text/csv", platform ? "agentos-audit-platform.csv" : $"agentos-audit-{tenant}.csv");
        }).RequirePermission(Permission.ViewAudit);

        return app;
    }
}
