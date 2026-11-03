// Enterprise review E0.1 / plan 0.9 / GAP-14 — Postgres row-level security on the tenant tables, stage 2 (ADR-0012).
// Application isolation (EF global filters + explicit tenant predicates) stays; RLS is the database-enforced
// second wall, so a forgotten filter or a raw SQL query fails closed instead of leaking another tenant's rows.
//
//   • Bootstrap (EnsureAsync, once per boot after migrations, under an advisory lock): a NOLOGIN, NOBYPASSRLS
//     role `agentos_rls`, DML grants on every module schema, and a policy on each tenant table:
//         visible/writable  ⇔  "TenantId" = app.tenant_id   OR   app.platform = 'on'
//     Stage 2: NO tenant ⇒ NOTHING (stage 1 let "no tenant" see every row). Work that is cross-tenant by nature
//     (the autopilot scan, the scheduler, the runner handshake, platform-scope audit rows) runs in an explicit,
//     counted PlatformScope(reason); work that knows its tenant from somewhere other than the caller (a signed
//     invitation, the row a background drain is writing) narrows to it with TenantScope(tenant).
//     FORCE ROW LEVEL SECURITY so the table owner is bound too (Azure connects as the owner, not a superuser).
//     config.app_config also lets every tenant READ the "__platform" rows (platform settings resolve under each
//     tenant's own); writing them needs the platform scope. tenants.audit_events is append-only for the app role
//     (REVOKE UPDATE, DELETE, TRUNCATE — GAP-11b).
//   • Per connection (ApplyToConnectionAsync, from the EF interceptor and the Dapper OpenWithTenantAsync helper):
//         SET ROLE agentos_rls; set_config('app.tenant_id', <tenant>); set_config('app.platform', 'on' | '')
//     SET ROLE matters locally: the dev/lab stack connects as the Postgres SUPERUSER, which bypasses RLS.
//     The tenant comes from the innermost TenantScope, else AmbientIdentity (runs, circuits — see the Web's
//     CircuitIdentityHandler), else the authenticated HTTP request. Npgsql resets session state when a pooled
//     connection is returned.
//   • Before the bootstrap ran (boot: migrations, seeds), and when RLS is off (Persistence:RowLevelSecurity=false,
//     or a failed bootstrap in Development), connections keep the login user and set app.platform = 'on', so
//     policies left by an earlier boot never hide rows from a deliberately un-isolated process. Outside Development a
//     failed bootstrap stops the host (EnsureAtStartupAsync) — it never runs with RLS silently off.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AgentOs.SharedKernel.Persistence;

/// <summary>Where row-level security stands in this process.</summary>
public enum RowLevelSecurityState
{
    /// <summary>The host has not run the bootstrap yet (boot). Connections run as the login user, platform-wide.</summary>
    Pending = 0,

    /// <summary>Bootstrapped: every connection is bound to the restricted role and its tenant.</summary>
    Enabled = 1,

    /// <summary>Turned off on purpose (<c>Persistence:RowLevelSecurity=false</c>): application filters only.</summary>
    Disabled = 2,

    /// <summary>The bootstrap failed (Development only — elsewhere the host stops): application filters only.</summary>
    Failed = 3,

    /// <summary>No database is configured.</summary>
    NotConfigured = 4,
}

/// <summary>The row-level-security bootstrap failed outside Development; the host must not start without it.</summary>
public sealed class RowLevelSecurityBootstrapException : InvalidOperationException
{
    /// <summary>Create the exception.</summary>
    public RowLevelSecurityBootstrapException() { }

    /// <summary>Create the exception with a message.</summary>
    public RowLevelSecurityBootstrapException(string message) : base(message) { }

    /// <summary>Create the exception with a message and inner exception.</summary>
    public RowLevelSecurityBootstrapException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Postgres row-level security for tenant tables (stage 2, ADR-0012).</summary>
public static partial class RowLevelSecurity
{
    /// <summary>The restricted role every application connection switches to.</summary>
    public const string Role = "agentos_rls";

    /// <summary>The session setting the policies compare <c>"TenantId"</c> with.</summary>
    public const string TenantSetting = "app.tenant_id";

    /// <summary>The session setting that opens every row to an explicit platform operation (<c>'on'</c>).</summary>
    public const string PlatformSetting = "app.platform";

    /// <summary>The reserved <c>config.app_config</c> tenant that holds platform settings (mirrors
    /// <c>SettingsService.PlatformTenant</c>; a test pins the two together). Every tenant may read these rows.</summary>
    public const string PlatformSettingsTenant = "__platform";

    /// <summary>Meter for the scope counters (register with <c>.AddMeter("AgentOs.Persistence")</c>).</summary>
    public const string MeterName = "AgentOs.Persistence";

    /// <summary>Instrument names, for tests and docs/OPERATIONS.md.</summary>
    public static class Instruments
    {
        /// <summary>Counter of connections bound by RLS, tagged <c>scope</c> = tenant | platform | none. A rising
        /// <c>none</c> is a code path that lost its tenant and now sees nothing (stage 2).</summary>
        public const string Connections = "agentos.rls.connections";

        /// <summary>Counter of explicit platform scopes entered, tagged <c>reason</c>.</summary>
        public const string PlatformScopes = "agentos.rls.platform_scope";
    }

    /// <summary>Tenant tables under RLS (schema, table). Every table a module adds with a <c>"TenantId"</c> column goes
    /// here or into <see cref="ExemptTables"/> with its reason (a real-Postgres test fails on a table in neither).</summary>
    public static IReadOnlyList<(string Schema, string Table)> Tables { get; } =
    [
        ("pipeline", "pipeline_runs"), ("pipeline", "run_metrics"), ("pipeline", "orchestrations"), ("pipeline", "scheduled_runs"),
        ("sessions", "sessions"), ("sessions", "session_repos"), ("sessions", "runners"), ("sessions", "ticket_files"),
        ("sessions", "design_reviews"),
        ("tools", "tool_invocations"),
        ("workspaces", "workspaces"), ("workspaces", "workspace_repos"), ("workspaces", "autopilot_events"), ("workspaces", "ticket_flows"),
        ("memory", "nodes"), ("memory", "edges"),
        ("config", "app_config"),
        ("tenants", "audit_events"), ("tenants", "invitations"),
    ];

    /// <summary>Tables with a <c>"TenantId"</c> column that are deliberately NOT under RLS, with the reason.</summary>
    public static IReadOnlyDictionary<string, string> ExemptTables { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tenants.api_tokens"] = "Looked up by its prefix while authenticating, before any tenant is known; EF filter + explicit tenant predicates, and only the SHA-256 of a token is stored.",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Tables the app role may only read and append to (GAP-11b): <c>REVOKE UPDATE, DELETE, TRUNCATE</c>.</summary>
    public static IReadOnlyList<(string Schema, string Table)> AppendOnlyTables { get; } = [("tenants", "audit_events")];

    /// <summary>Tables whose rows under a reserved tenant id every tenant may read (schema, table, reserved id).</summary>
    public static IReadOnlyList<(string Schema, string Table, string ReadableTenant)> SharedReadRows { get; } =
        [("config", "app_config", PlatformSettingsTenant)];

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> ConnectionCounter =
        Meter.CreateCounter<long>(Instruments.Connections, unit: "{connection}", description: "Connections bound by row-level security, by scope (tenant, platform, none).");
    private static readonly Counter<long> PlatformScopeCounter =
        Meter.CreateCounter<long>(Instruments.PlatformScopes, unit: "{scope}", description: "Explicit platform-wide RLS scopes entered, by reason.");

    private static readonly AsyncLocal<Frame?> _scope = new();
    private static int _state = (int)RowLevelSecurityState.Pending;

    /// <summary>Where RLS stands in this process.</summary>
    public static RowLevelSecurityState State => (RowLevelSecurityState)Volatile.Read(ref _state);

    /// <summary>True once <see cref="EnsureAsync"/> succeeded in this process.</summary>
    public static bool Enabled => State == RowLevelSecurityState.Enabled;

    /// <summary>Resolves the authenticated HTTP request's tenant when no ambient identity is pushed (set by the host).</summary>
    public static Func<string?>? HttpTenant { get; set; }

    /// <summary>True inside <see cref="PlatformScope"/> (the innermost scope wins).</summary>
    public static bool InPlatformScope => _scope.Value is { Platform: true };

    /// <summary>Open every tenant's rows to this async flow — for operations that are cross-tenant by nature (a platform
    /// worker's scan, the runner handshake, platform-scope audit rows). Counted by <paramref name="reason"/> on
    /// <c>agentos.rls.platform_scope</c>. The connection still runs as the restricted role: append-only tables stay
    /// append-only. Dispose to restore the previous scope.</summary>
    public static IDisposable PlatformScope(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        PlatformScopeCounter.Add(1, new KeyValuePair<string, object?>("reason", reason));
        return Push(new Frame(null, Platform: true));
    }

    /// <summary>Bind this async flow to <paramref name="tenantId"/>, whatever the ambient or HTTP tenant is — for work that
    /// knows its tenant from a trusted source other than the caller (a signed invitation token, the row a background
    /// drain writes, an audit entry's own tenant). Dispose to restore the previous scope.</summary>
    public static IDisposable TenantScope(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return Push(new Frame(tenantId, Platform: false));
    }

    /// <summary>Bind to <paramref name="tenantId"/> only when this flow acts for no tenant yet (a background worker, an
    /// anonymous request): an explicit tenant FILLS a missing one and never overrides the flow's own, so a flow bound to
    /// tenant A that names tenant B still sees nothing of B. Null (nothing to dispose) when the flow is already bound, is
    /// a platform operation, or <paramref name="tenantId"/> is blank.</summary>
    public static IDisposable? TenantScopeIfUnbound(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) || InPlatformScope || CurrentTenant() is not null
            ? null
            : TenantScope(tenantId);

    /// <summary>The tenant the current flow acts for, or null when unknown (or inside a platform scope).</summary>
    public static string? CurrentTenant()
        => _scope.Value is { } frame
            ? frame.Tenant
            : AmbientIdentity.Current?.TenantId is { Length: > 0 } ambient ? ambient : HttpTenant?.Invoke();

    /// <summary>Bind an opened connection: the restricted role + the flow's scope once RLS is enabled; before that (boot)
    /// or when RLS is off, the login user platform-wide (see the file header).</summary>
    public static Task ApplyToConnectionAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection is not NpgsqlConnection npgsql || State is RowLevelSecurityState.NotConfigured) { return Task.CompletedTask; }
        return Enabled ? ApplySessionAsync(npgsql, switchRole: true, cancellationToken) : ApplyUnboundAsync(npgsql, cancellationToken);
    }

    /// <summary>Synchronous twin for EF's synchronous open path.</summary>
    public static void ApplyToConnection(DbConnection connection)
    {
        if (connection is not NpgsqlConnection npgsql || State is RowLevelSecurityState.NotConfigured) { return; }
        using var cmd = Enabled ? ScopeCommand(npgsql, switchRole: true) : UnboundCommand(npgsql);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Bind <paramref name="connection"/> to the current flow's scope — its tenant, an explicit platform scope, or
    /// (neither) nothing — switching to <see cref="Role"/> when <paramref name="switchRole"/>. Without the role switch the
    /// login user keeps its own privileges (a privileged maintenance connection such as the tenant purge) but stays bound
    /// by the forced policies when it owns the tables. Independent of <see cref="State"/>.</summary>
    public static async Task ApplySessionAsync(NpgsqlConnection connection, bool switchRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var cmd = ScopeCommand(connection, switchRole);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyUnboundAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = UnboundCommand(connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlCommand ScopeCommand(NpgsqlConnection connection, bool switchRole)
    {
        var platform = InPlatformScope;
        var tenant = platform ? null : CurrentTenant();
        ConnectionCounter.Add(1, new KeyValuePair<string, object?>("scope", platform ? "platform" : tenant is null ? "none" : "tenant"));
        var cmd = connection.CreateCommand();
        cmd.CommandText = (switchRole ? $"SET ROLE {Role}; " : string.Empty)
            + $"SELECT set_config('{TenantSetting}', @tenant, false), set_config('{PlatformSetting}', @platform, false)";
        cmd.Parameters.AddWithValue("tenant", tenant ?? string.Empty);
        cmd.Parameters.AddWithValue("platform", platform ? "on" : string.Empty);
        return cmd;
    }

    // Not bootstrapped yet (boot: migrations, seeds) or deliberately off: the login user, platform-wide, so policies an
    // earlier boot left behind never hide rows from a process that runs without the database wall on purpose.
    private static NpgsqlCommand UnboundCommand(NpgsqlConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT set_config('{TenantSetting}', '', false), set_config('{PlatformSetting}', 'on', false)";
        return cmd;
    }

    /// <summary>Open a Dapper connection and apply RLS to it.</summary>
    public static async Task<DbConnection> OpenWithTenantAsync(this INpgsqlConnectionFactory factory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var connection = factory.Create();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ApplyToConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Open a Dapper connection for a tenant-explicit read: bound to the flow's own tenant, or — when the flow acts
    /// for no tenant (a background worker, the runner hub) — to <paramref name="tenantId"/>
    /// (<see cref="TenantScopeIfUnbound"/>). The session is set when the connection opens, so the scope ends here.</summary>
    public static async Task<DbConnection> OpenForTenantAsync(this INpgsqlConnectionFactory factory, string? tenantId, CancellationToken cancellationToken = default)
    {
        using var bound = TenantScopeIfUnbound(tenantId);
        return await factory.OpenWithTenantAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The host's boot step: bootstrap RLS after every module migrated. No database → nothing to do;
    /// <paramref name="enabledByConfig"/> false (the <c>Persistence:RowLevelSecurity</c> escape hatch) → off, logged as a
    /// warning on every boot; a failed bootstrap → <see cref="RowLevelSecurityBootstrapException"/> outside Development
    /// (the host must not serve tenants without the database wall), an error log and application filters only in
    /// Development.</summary>
    public static async Task<RowLevelSecurityState> EnsureAtStartupAsync(
        string? connectionString, bool enabledByConfig, bool isDevelopment, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            SetState(RowLevelSecurityState.NotConfigured);
            return State;
        }
        if (!enabledByConfig)
        {
            SetState(RowLevelSecurityState.Disabled);
            LogDisabled(logger);
            return State;
        }
        if (await EnsureAsync(connectionString, logger, cancellationToken).ConfigureAwait(false))
        {
            return State;
        }
        if (!isDevelopment)
        {
            throw new RowLevelSecurityBootstrapException(
                "Row-level security could not be set up, so AgentOS.NET will not start: tenant isolation would rest on application "
                + "filters alone. Give the database user CREATEROLE and ownership of the module tables (or have a DBA create the "
                + $"'{Role}' role and grant it), then restart. To run without it on purpose, set Persistence:RowLevelSecurity=false.");
        }
        return State;
    }

    /// <summary>Create the role, grants and policies (idempotent), then bind every connection. Returns false (state
    /// <see cref="RowLevelSecurityState.Failed"/>, logged as an error) when the database refuses. A deadlock or lock
    /// timeout against live traffic (a rolling deploy) is retried twice.</summary>
    public static async Task<bool> EnsureAsync(string? connectionString, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            SetState(RowLevelSecurityState.NotConfigured);
            return false;
        }
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var gate = await PgAdvisoryLock.AcquireAsync(connectionString, "agentos:rls-bootstrap", cancellationToken).ConfigureAwait(false);
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = BootstrapSql(conn.PostgreSqlVersion.Major);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                SetState(RowLevelSecurityState.Enabled);
                LogEnabled(logger, Tables.Count);
                return true;
            }
            catch (PostgresException ex) when (attempt < 3 && ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable)
            {
                LogRetry(logger, attempt, ex.SqlState);
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                SetState(RowLevelSecurityState.Failed);
                LogFailed(logger, ex);
                return false;
            }
        }
    }

    // Internal for tests that drive the startup paths and must put the process-wide state back afterwards.
    internal static void SetState(RowLevelSecurityState state) => Volatile.Write(ref _state, (int)state);

    /// <summary>The policy predicate: the row's tenant is the session's tenant (an empty setting matches nothing), or
    /// the session is an explicit platform operation.</summary>
    public static string PolicyPredicate { get; } =
        "(\"TenantId\" = NULLIF(current_setting('" + TenantSetting + "', true), '') OR current_setting('" + PlatformSetting + "', true) = 'on')";

    /// <summary>The idempotent bootstrap script (exposed for tests and the ADR).</summary>
    public static string BootstrapSql(int serverMajorVersion)
    {
        var sb = new System.Text.StringBuilder(8192);
        sb.Append($"""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Role}') THEN
                CREATE ROLE {Role} NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
              END IF;
            END $$;
            {(serverMajorVersion >= 16 ? $"GRANT {Role} TO CURRENT_USER WITH INHERIT FALSE, SET TRUE;" : $"GRANT {Role} TO CURRENT_USER;")}
            DO $$ DECLARE s text; BEGIN
              FOR s IN SELECT DISTINCT table_schema FROM information_schema.tables WHERE table_name LIKE '\_\_%MigrationsHistory' LOOP
                EXECUTE format('GRANT USAGE ON SCHEMA %I TO {Role}', s);
                EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO {Role}', s);
                EXECUTE format('GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA %I TO {Role}', s);
                EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {Role}', s);
                EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {Role}', s);
              END LOOP;
            END $$;

            """);
        // GAP-11b: after the schema-wide grants above (which run every boot), take the write-back rights away again.
        foreach (var (schema, table) in AppendOnlyTables)
        {
            sb.Append($"""
                DO $$ BEGIN
                  IF to_regclass('{schema}.{table}') IS NOT NULL THEN
                    REVOKE UPDATE, DELETE, TRUNCATE ON "{schema}"."{table}" FROM {Role};
                  END IF;
                END $$;

                """);
        }
        foreach (var (schema, table) in Tables)
        {
            var t = $"\"{schema}\".\"{table}\"";
            var sharedRead = string.Concat(SharedReadRows
                .Where(r => r.Schema == schema && r.Table == table)
                .Select(r => $"\n    CREATE POLICY agentos_shared_read ON {t} FOR SELECT USING (\"TenantId\" = '{r.ReadableTenant}');"));
            // One DO block per table = one transaction: the policy is never missing while live traffic queries the table.
            sb.Append($"""
                DO $$ BEGIN
                  IF to_regclass('{schema}.{table}') IS NOT NULL THEN
                    ALTER TABLE {t} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE {t} FORCE ROW LEVEL SECURITY;
                    DROP POLICY IF EXISTS agentos_tenant ON {t};
                    DROP POLICY IF EXISTS agentos_shared_read ON {t};
                    CREATE POLICY agentos_tenant ON {t} USING {PolicyPredicate} WITH CHECK {PolicyPredicate};{sharedRead}
                  END IF;
                END $$;

                """);
        }
        return sb.ToString();
    }

    private static Restore Push(Frame frame)
    {
        var previous = _scope.Value;
        _scope.Value = frame;
        return new Restore(previous);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Row-level security enabled on {Count} tenant tables (stage 2: no tenant sees nothing).")]
    private static partial void LogEnabled(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Row-level security bootstrap FAILED — tenant isolation falls back to application filters only. Check the database user's privileges (CREATEROLE, table ownership).")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Row-level security bootstrap hit {SqlState} on attempt {Attempt} (live traffic holds a lock) — retrying.")]
    private static partial void LogRetry(ILogger logger, int attempt, string sqlState);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Row-level security is OFF (Persistence:RowLevelSecurity=false): tenant isolation rests on application filters only.")]
    private static partial void LogDisabled(ILogger logger);

    private sealed record Frame(string? Tenant, bool Platform);

    private sealed class Restore(Frame? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _scope.Value = previous;
        }
    }
}
