// ADR-0012 stage 2 (GAP-14) — the bootstrap script covers every tenant table with an enabled + forced policy whose
// predicate shows a session with no tenant NOTHING (stage 1 showed it everything); config.app_config alone shares its
// "__platform" rows for reading; tenants.audit_events is append-only for the app role; the PG16 membership syntax is
// used only where it exists. The per-connection scope resolves innermost-first (tenant / platform scopes, then the
// ambient identity, then the HTTP request) and a fill-in scope never overrides a flow's own tenant. A failed bootstrap
// stops the host outside Development. The database-level behaviour is proven on real Postgres by
// RowLevelSecurityStageTwoRealPostgresTests (every table) and the E2E RowLevelSecurityRealDbTests (the running stack).

using System;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.ServiceDefaults;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Persistence;

// Some tests here drive the process-wide RowLevelSecurity.State (and restore it): they run with the other tests that do.
[Collection(RowLevelSecurityProcessStateGroup.Name)]
public sealed class RowLevelSecurityTests
{
    [Fact]
    public void BootstrapSql_PolicesEveryTenantTable_EnabledAndForced_WithTheStageTwoPredicate()
    {
        var sql = RowLevelSecurity.BootstrapSql(18);
        foreach (var (schema, table) in RowLevelSecurity.Tables)
        {
            sql.ShouldContain($"ALTER TABLE \"{schema}\".\"{table}\" ENABLE ROW LEVEL SECURITY;");
            sql.ShouldContain($"ALTER TABLE \"{schema}\".\"{table}\" FORCE ROW LEVEL SECURITY;");
            sql.ShouldContain($"CREATE POLICY agentos_tenant ON \"{schema}\".\"{table}\" USING {RowLevelSecurity.PolicyPredicate} WITH CHECK {RowLevelSecurity.PolicyPredicate};");
        }
        sql.ShouldContain("NOBYPASSRLS");
    }

    // The stage-1 escape — "no tenant set ⇒ every row" — is gone: an empty or unset tenant matches no row (NULLIF), and
    // only an explicit platform flag opens every tenant.
    [Fact]
    public void PolicyPredicate_NoTenantMatchesNothing_OnlyAnExplicitPlatformScopeWidens()
    {
        RowLevelSecurity.PolicyPredicate.ShouldBe(
            "(\"TenantId\" = NULLIF(current_setting('app.tenant_id', true), '') OR current_setting('app.platform', true) = 'on')");
        RowLevelSecurity.BootstrapSql(18).ShouldNotContain("coalesce(current_setting");
    }

    // Stage 2 brings the platform-scoped tables (app_config, audit_events) + invitations under the policy; the tables the
    // modules added in the meantime (scheduled runs, memory, autopilot events, ticket flows/files, design reviews,
    // approvals) are covered as well — a table a module adds goes into Tables or ExemptTables, never nowhere.
    [Fact]
    public void Tables_StageTwo_IncludeAppConfigAuditEventsAndInvitations()
    {
        RowLevelSecurity.Tables.Count.ShouldBe(20);
        RowLevelSecurity.Tables.ShouldContain(("config", "app_config"));
        RowLevelSecurity.Tables.ShouldContain(("tenants", "audit_events"));
        RowLevelSecurity.Tables.ShouldContain(("tenants", "invitations"));
        RowLevelSecurity.Tables.ShouldContain(("pipeline", "scheduled_runs"));
        RowLevelSecurity.Tables.ShouldContain(("sessions", "ticket_files"));
        RowLevelSecurity.Tables.ShouldContain(("sessions", "design_reviews"));
        RowLevelSecurity.Tables.ShouldContain(("sessions", "approvals"));
        RowLevelSecurity.Tables.ShouldContain(("workspaces", "ticket_flows"));
        RowLevelSecurity.Tables.ShouldContain(("memory", "nodes"));
        RowLevelSecurity.Tables.Distinct().Count().ShouldBe(RowLevelSecurity.Tables.Count);
        RowLevelSecurity.ExemptTables.Keys.ShouldBe(["tenants.api_tokens"]);
        RowLevelSecurity.ExemptTables.Keys.ShouldNotContain(k => RowLevelSecurity.Tables.Any(t => $"{t.Schema}.{t.Table}" == k));
    }

    // Platform settings are read by every tenant (they are the defaults behind each tenant's own), never written by one.
    [Fact]
    public void BootstrapSql_AppConfigSharesPlatformRowsForReadingOnly()
    {
        var sql = RowLevelSecurity.BootstrapSql(18);
        sql.ShouldContain("CREATE POLICY agentos_shared_read ON \"config\".\"app_config\" FOR SELECT USING (\"TenantId\" = '__platform');");
        sql.Split("CREATE POLICY agentos_shared_read").Length.ShouldBe(2);   // created for app_config only (dropped everywhere)
        RowLevelSecurity.PlatformSettingsTenant.ShouldBe(AgentOs.Modules.AppConfig.Settings.SettingsService.PlatformTenant);
    }

    // GAP-11b: the revoke runs AFTER the schema-wide grants (which run every boot), or it would be undone each boot.
    [Fact]
    public void BootstrapSql_AuditEventsAreAppendOnly_RevokedAfterTheGrants()
    {
        var sql = RowLevelSecurity.BootstrapSql(18);
        const string revoke = "REVOKE UPDATE, DELETE, TRUNCATE ON \"tenants\".\"audit_events\" FROM agentos_rls;";
        sql.ShouldContain(revoke);
        sql.IndexOf(revoke, StringComparison.Ordinal).ShouldBeGreaterThan(sql.IndexOf("GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES", StringComparison.Ordinal));
        RowLevelSecurity.AppendOnlyTables.ShouldBe([("tenants", "audit_events")]);
    }

    [Fact]
    public void BootstrapSql_UsesTheMembershipSyntaxTheServerSupports()
    {
        RowLevelSecurity.BootstrapSql(16).ShouldContain("WITH INHERIT FALSE, SET TRUE");
        RowLevelSecurity.BootstrapSql(15).ShouldNotContain("SET TRUE");
    }

    [Fact]
    public void CurrentTenant_PrefersTheAmbientIdentity_ThenTheHttpRequest()
    {
        var previous = RowLevelSecurity.HttpTenant;
        try
        {
            RowLevelSecurity.HttpTenant = () => "from-http";
            RowLevelSecurity.CurrentTenant().ShouldBe("from-http");
            using (AmbientIdentity.Push("from-run", "u"))
            {
                RowLevelSecurity.CurrentTenant().ShouldBe("from-run");
            }
            RowLevelSecurity.HttpTenant = null;
            RowLevelSecurity.CurrentTenant().ShouldBeNull();
        }
        finally
        {
            RowLevelSecurity.HttpTenant = previous;
        }
    }

    // Innermost scope wins; disposing restores the outer one.
    [Fact]
    public void Scopes_InnermostWins_AndRestoreOnDispose()
    {
        using (AmbientIdentity.Push("ambient", "u"))
        {
            using (RowLevelSecurity.PlatformScope("test"))
            {
                RowLevelSecurity.InPlatformScope.ShouldBeTrue();
                RowLevelSecurity.CurrentTenant().ShouldBeNull();
                using (RowLevelSecurity.TenantScope("signed-token-tenant"))
                {
                    RowLevelSecurity.InPlatformScope.ShouldBeFalse();
                    RowLevelSecurity.CurrentTenant().ShouldBe("signed-token-tenant");
                }
                RowLevelSecurity.InPlatformScope.ShouldBeTrue();
            }
            RowLevelSecurity.InPlatformScope.ShouldBeFalse();
            RowLevelSecurity.CurrentTenant().ShouldBe("ambient");
        }
    }

    // An explicit tenant fills a flow that has none; it never overrides a flow's own tenant, nor a platform scope.
    [Fact]
    public void TenantScopeIfUnbound_FillsOnlyAMissingTenant()
    {
        var previous = RowLevelSecurity.HttpTenant;
        try
        {
            RowLevelSecurity.HttpTenant = null;
            using (RowLevelSecurity.TenantScopeIfUnbound("worker-row-tenant"))
            {
                RowLevelSecurity.CurrentTenant().ShouldBe("worker-row-tenant");
            }
            RowLevelSecurity.CurrentTenant().ShouldBeNull();

            using (AmbientIdentity.Push("tenant-a", "u"))
            {
                RowLevelSecurity.TenantScopeIfUnbound("tenant-b").ShouldBeNull();
                RowLevelSecurity.CurrentTenant().ShouldBe("tenant-a");
            }
            using (RowLevelSecurity.PlatformScope("test"))
            {
                RowLevelSecurity.TenantScopeIfUnbound("tenant-b").ShouldBeNull();
            }
            RowLevelSecurity.TenantScopeIfUnbound("  ").ShouldBeNull();
        }
        finally
        {
            RowLevelSecurity.HttpTenant = previous;
        }
    }

    [Fact]
    public async Task ScopeSet_InAnAsyncMethod_DoesNotLeakToTheCaller()
    {
        static async Task EnterAsync()
        {
            using var platform = RowLevelSecurity.PlatformScope("test");
            await Task.Yield();
            RowLevelSecurity.InPlatformScope.ShouldBeTrue();
        }

        await EnterAsync();
        RowLevelSecurity.InPlatformScope.ShouldBeFalse();
    }

    [Fact]
    public void Scopes_RejectBlankArguments()
    {
        Should.Throw<ArgumentException>(() => RowLevelSecurity.PlatformScope(" "));
        Should.Throw<ArgumentException>(() => RowLevelSecurity.TenantScope(""));
    }

    // GAP-14: a database the bootstrap can't reach stops the host outside Development; Development logs and goes on.
    // (Port 1 refuses at once — no real server needed. No database / the escape hatch never touch the network.)
    // The state is process-wide: these two put it back (other tests only ever see a "not enabled" state either way).
    [Fact]
    public async Task EnsureAtStartup_BootstrapFails_OutsideDevelopment_Throws()
    {
        var previous = RowLevelSecurity.State;
        try
        {
            var error = await Should.ThrowAsync<RowLevelSecurityBootstrapException>(() =>
                RowLevelSecurity.EnsureAtStartupAsync(Unreachable, enabledByConfig: true, isDevelopment: false, NullLogger.Instance));
            error.Message.ShouldContain("Persistence:RowLevelSecurity=false");
            RowLevelSecurity.State.ShouldBe(RowLevelSecurityState.Failed);
        }
        finally
        {
            RowLevelSecurity.SetState(previous);
        }
    }

    [Fact]
    public async Task EnsureAtStartup_DevelopmentOrHatchOrNoDatabase_DoesNotThrow()
    {
        var previous = RowLevelSecurity.State;
        try
        {
            (await RowLevelSecurity.EnsureAtStartupAsync(Unreachable, enabledByConfig: true, isDevelopment: true, NullLogger.Instance))
                .ShouldBe(RowLevelSecurityState.Failed);
            (await RowLevelSecurity.EnsureAtStartupAsync(Unreachable, enabledByConfig: false, isDevelopment: false, NullLogger.Instance))
                .ShouldBe(RowLevelSecurityState.Disabled);
            (await RowLevelSecurity.EnsureAtStartupAsync(null, enabledByConfig: true, isDevelopment: false, NullLogger.Instance))
                .ShouldBe(RowLevelSecurityState.NotConfigured);
        }
        finally
        {
            RowLevelSecurity.SetState(previous);
        }
    }

    private const string Unreachable = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2";

    [Theory]
    [InlineData(RowLevelSecurityState.Enabled, HealthStatus.Healthy)]
    [InlineData(RowLevelSecurityState.Disabled, HealthStatus.Degraded)]
    [InlineData(RowLevelSecurityState.Pending, HealthStatus.Degraded)]
    [InlineData(RowLevelSecurityState.Failed, HealthStatus.Unhealthy)]
    [InlineData(RowLevelSecurityState.NotConfigured, HealthStatus.Healthy)]
    public void HealthCheck_ReportsTheState(RowLevelSecurityState state, HealthStatus expected)
        => RowLevelSecurityHealthCheck.For(state, HealthStatus.Unhealthy).Status.ShouldBe(expected);
}
