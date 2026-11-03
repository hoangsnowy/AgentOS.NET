// GAP-14 — the one place both hosts bring up Postgres row-level security (ADR-0012), after every module migrated:
// the HTTP-tenant resolver, the bootstrap (fails the boot outside Development), and a readiness check that reports
// whether the database wall is actually up.
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace AgentOs.ServiceDefaults;

/// <summary>Host wiring for <see cref="RowLevelSecurity"/>.</summary>
public static class RowLevelSecurityStartup
{
    /// <summary>The bootstrap-only escape hatch (<c>false</c> = application filters only).</summary>
    public const string ConfigKey = "Persistence:RowLevelSecurity";

    /// <summary>Bind connections to the caller's tenant (an authenticated request's claim, or the ambient identity a run /
    /// desktop circuit pushed) and bootstrap RLS. Throws <see cref="RowLevelSecurityBootstrapException"/> outside
    /// Development when the bootstrap fails — the host must not serve tenants with RLS silently off.</summary>
    public static async Task<RowLevelSecurityState> UseAgentOsRowLevelSecurityAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var httpAccessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        RowLevelSecurity.HttpTenant = () =>
            httpAccessor.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user.GetTenantId() : null;
        return await RowLevelSecurity.EnsureAtStartupAsync(
            app.Configuration.GetConnectionString("DefaultConnection"),
            app.Configuration.GetValue(ConfigKey, true),
            app.Environment.IsDevelopment(),
            app.Logger).ConfigureAwait(false);
    }
}

/// <summary>Readiness: is row-level security bound? Registered only when a database is configured.</summary>
internal sealed class RowLevelSecurityHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(For(RowLevelSecurity.State, context.Registration.FailureStatus));
    }

    /// <summary>The check's answer for a state (pure, for tests).</summary>
    internal static HealthCheckResult For(RowLevelSecurityState state, HealthStatus failureStatus) => state switch
    {
        RowLevelSecurityState.Enabled => HealthCheckResult.Healthy($"Row-level security is on for {RowLevelSecurity.Tables.Count} tenant tables."),
        RowLevelSecurityState.Disabled => HealthCheckResult.Degraded($"Row-level security is turned off ({RowLevelSecurityStartup.ConfigKey}=false): tenant isolation rests on application filters only."),
        RowLevelSecurityState.Failed => new HealthCheckResult(failureStatus, "Row-level security failed to start: tenant isolation rests on application filters only."),
        RowLevelSecurityState.Pending => HealthCheckResult.Degraded("Row-level security has not been set up yet."),
        _ => HealthCheckResult.Healthy("No database is configured."),
    };
}
