// Enterprise review E0.1 / plan 0.9 — Postgres row-level security on the tenant tables, stage 1 (ADR-0012).
// Application isolation (EF global filters + explicit tenant predicates) stays; RLS is the database-enforced
// second wall, so a forgotten filter or a raw SQL query fails closed instead of leaking another tenant's rows.
//
//   • Bootstrap (EnsureAsync, once per boot after migrations, under an advisory lock): a NOLOGIN, NOBYPASSRLS
//     role `agentos_rls`, DML grants on every module schema, and a policy on each tenant table:
//         visible/writable  ⇔  "TenantId" = app.tenant_id   — or app.tenant_id is unset (stage 1: work with no
//         known tenant — pairing, background drains, boot — keeps working).
//     FORCE ROW LEVEL SECURITY so the table owner is bound too (Azure connects as the owner, not a superuser).
//   • Per connection (ApplyToConnectionAsync, from the EF interceptor and the Dapper OpenAsync helper):
//         SET ROLE agentos_rls; set_config('app.tenant_id', <tenant>)
//     SET ROLE matters locally: the dev/lab stack connects as the Postgres SUPERUSER, which bypasses RLS.
//     The tenant comes from AmbientIdentity (runs, circuits — see the Web's CircuitIdentityHandler), else the
//     authenticated HTTP request. Npgsql resets session state when a pooled connection is returned.
//   • Stage 2 (not here): config.app_config and tenants.audit_events (they hold platform-scope rows), and
//     tightening "unset ⇒ visible" to "unset ⇒ nothing" once every path pushes a tenant.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AgentOs.SharedKernel.Persistence;

/// <summary>Postgres row-level security for tenant tables (stage 1).</summary>
public static partial class RowLevelSecurity
{
    /// <summary>The restricted role every application connection switches to.</summary>
    public const string Role = "agentos_rls";

    /// <summary>The session setting the policies compare <c>"TenantId"</c> with.</summary>
    public const string TenantSetting = "app.tenant_id";

    /// <summary>Tenant tables under RLS in stage 1 (schema, table).</summary>
    public static IReadOnlyList<(string Schema, string Table)> Tables { get; } =
    [
        ("pipeline", "pipeline_runs"), ("pipeline", "run_metrics"), ("pipeline", "orchestrations"), ("pipeline", "scheduled_runs"),
        ("sessions", "sessions"), ("sessions", "session_repos"), ("sessions", "runners"),
        ("tools", "tool_invocations"),
        ("workspaces", "workspaces"), ("workspaces", "workspace_repos"), ("workspaces", "autopilot_events"),
        ("memory", "nodes"), ("memory", "edges"),
    ];

    private static volatile bool _enabled;
    private static readonly AsyncLocal<bool> _bypass = new();

    /// <summary>True once <see cref="EnsureAsync"/> succeeded in this process.</summary>
    public static bool Enabled => _enabled;

    /// <summary>Resolves the authenticated HTTP request's tenant when no ambient identity is pushed (set by the host).</summary>
    public static Func<string?>? HttpTenant { get; set; }

    /// <summary>Skip the role/tenant switch for this async flow (migrations, platform maintenance).</summary>
    public static IDisposable Bypass()
    {
        var previous = _bypass.Value;
        _bypass.Value = true;
        return new Restore(previous);
    }

    /// <summary>The tenant the current flow acts for, or null when unknown.</summary>
    public static string? CurrentTenant()
        => AmbientIdentity.Current?.TenantId is { Length: > 0 } ambient ? ambient : HttpTenant?.Invoke();

    /// <summary>Switch an opened connection to the restricted role and the current tenant.</summary>
    public static async Task ApplyToConnectionAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        if (!_enabled || _bypass.Value || connection is not NpgsqlConnection npgsql) { return; }
        await using var cmd = Command(npgsql);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous twin for EF's synchronous open path.</summary>
    public static void ApplyToConnection(DbConnection connection)
    {
        if (!_enabled || _bypass.Value || connection is not NpgsqlConnection npgsql) { return; }
        using var cmd = Command(npgsql);
        cmd.ExecuteNonQuery();
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = $"SET ROLE {Role}; SELECT set_config('{TenantSetting}', @tenant, false)";
        cmd.Parameters.AddWithValue("tenant", CurrentTenant() ?? string.Empty);
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

    /// <summary>Create the role, grants and policies (idempotent), then enable the per-connection switch.
    /// Returns false (RLS stays off, logged as an error) when the database refuses — never stops the host.</summary>
    public static async Task<bool> EnsureAsync(string? connectionString, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(connectionString)) { return false; }
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
            _enabled = true;
            LogEnabled(logger, Tables.Count);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            LogFailed(logger, ex);
            return false;
        }
    }

    /// <summary>The idempotent bootstrap script (exposed for tests and the ADR).</summary>
    public static string BootstrapSql(int serverMajorVersion)
    {
        var sb = new System.Text.StringBuilder(4096);
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
        const string predicate = "(\"TenantId\" = current_setting('" + TenantSetting + "', true) OR coalesce(current_setting('" + TenantSetting + "', true), '') = '')";
        foreach (var (schema, table) in Tables)
        {
            var t = $"\"{schema}\".\"{table}\"";
            sb.Append($"""
                DO $$ BEGIN
                  IF to_regclass('{schema}.{table}') IS NOT NULL THEN
                    ALTER TABLE {t} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE {t} FORCE ROW LEVEL SECURITY;
                    DROP POLICY IF EXISTS agentos_tenant ON {t};
                    CREATE POLICY agentos_tenant ON {t} USING {predicate} WITH CHECK {predicate};
                  END IF;
                END $$;

                """);
        }
        return sb.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Row-level security enabled on {Count} tenant tables (stage 1).")]
    private static partial void LogEnabled(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Row-level security bootstrap FAILED — tenant isolation falls back to application filters only. Check the database user's privileges (CREATEROLE, table ownership).")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => _bypass.Value = previous;
    }
}
