// Enterprise review E1.4 — a tenant's data, end to end: EXPORT everything it owns (GDPR access / portability,
// offboarding) and PURGE everything it owns (right to erasure, contract end). Catalog-driven, so a table a future
// module adds is covered with no code change: every table with a "TenantId" column, in every schema.
//   • Export streams one JSON document {tenant, exportedAtUtc, tables: {"schema.table": [rows…]}} — rows via
//     to_jsonb with secret columns dropped (config values are encrypted secrets, runner token hashes).
//   • Purge runs in ONE transaction: tables in foreign-key order (children before parents, Kahn's algorithm over
//     pg_constraint), then the registry row; Keycloak users of the tenant after commit; audited under _platform.
// Runs on a raw connection (no RLS role switch); every statement is filtered by the tenant explicitly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants.Keycloak;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AgentOs.Modules.Tenants.Data;

/// <summary>Rows deleted per table, users removed, and when.</summary>
public sealed record TenantPurgeReport(string TenantId, IReadOnlyList<(string Table, long Rows)> Tables, int KeycloakUsers, DateTimeOffset AtUtc)
{
    /// <summary>Total rows deleted.</summary>
    public long TotalRows => Tables.Sum(t => t.Rows);
}

/// <summary>Export and purge a tenant's data.</summary>
public interface ITenantDataService
{
    /// <summary>True when a database is configured (no DB → nothing to export or purge).</summary>
    bool IsAvailable { get; }

    /// <summary>Write the tenant's data as one JSON document to <paramref name="output"/>.</summary>
    Task ExportAsync(string tenantId, Stream output, CancellationToken ct = default);

    /// <summary>Delete every row the tenant owns, its registry row and its Keycloak users.</summary>
    Task<TenantPurgeReport> PurgeAsync(string tenantId, string actor, CancellationToken ct = default);
}

internal sealed partial class TenantDataService(
    string? connectionString, IKeycloakAdminClient keycloak, IAuditLog audit, TimeProvider clock, ILogger<TenantDataService> logger) : ITenantDataService
{
    // Columns never exported: encrypted secrets and credential material.
    private static readonly Dictionary<string, string[]> Redacted = new(StringComparer.Ordinal)
    {
        ["config.app_config"] = ["Value"],
        ["sessions.runners"] = ["TokenHash"],
    };

    public bool IsAvailable => !string.IsNullOrWhiteSpace(connectionString);

    public async Task ExportAsync(string tenantId, Stream output, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(output);
        EnsureAvailable();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var tables = await TenantTablesAsync(conn, ct).ConfigureAwait(false);

        await using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("tenant", tenantId);
        json.WriteString("exportedAtUtc", clock.GetUtcNow());
        json.WriteStartObject("tables");
        foreach (var table in tables.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            json.WriteStartArray(table.Name);
            var drop = Redacted.TryGetValue(table.Name, out var cols) ? string.Concat(cols.Select(c => $" - '{c}'")) : string.Empty;
            await using var cmd = new NpgsqlCommand($"SELECT (to_jsonb(t){drop})::text FROM {table.Quoted} t WHERE \"TenantId\" = @t", conn);
            cmd.Parameters.AddWithValue("t", tenantId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                json.WriteRawValue(reader.GetString(0), skipInputValidation: true);
            }
            json.WriteEndArray();
            await json.FlushAsync(ct).ConfigureAwait(false);
        }
        json.WriteEndObject();
        json.WriteEndObject();
        await json.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<TenantPurgeReport> PurgeAsync(string tenantId, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        EnsureAvailable();
        var deleted = new List<(string Table, long Rows)>();
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var tables = await TenantTablesAsync(conn, ct).ConfigureAwait(false);
            var order = DeleteOrder(tables, await ForeignKeysAsync(conn, ct).ConfigureAwait(false));
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var table in order)
            {
                await using var cmd = new NpgsqlCommand($"DELETE FROM {table.Quoted} WHERE \"TenantId\" = @t", conn, tx);
                cmd.Parameters.AddWithValue("t", tenantId);
                var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows > 0) { deleted.Add((table.Name, rows)); }
            }
            await using (var registry = new NpgsqlCommand("DELETE FROM tenants.tenants WHERE \"Id\" = @t", conn, tx))
            {
                registry.Parameters.AddWithValue("t", tenantId);
                var rows = await registry.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows > 0) { deleted.Add(("tenants.tenants", rows)); }
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        // Identity after the data: a failure here leaves users without data, never data without an owner record.
        var users = 0;
        foreach (var user in await keycloak.ListUsersByTenantAsync(tenantId, max: 1000, ct).ConfigureAwait(false))
        {
            await keycloak.DeleteUserAsync(user.Id, ct).ConfigureAwait(false);
            users++;
        }

        var report = new TenantPurgeReport(tenantId, deleted, users, clock.GetUtcNow());
        await audit.WriteAsync(new AuditEntry(Guid.NewGuid(), AuditScopes.Platform, actor, AuditActions.TenantPurged,
            $"{tenantId}: {report.TotalRows} rows in {deleted.Count} tables, {users} users", null, null, report.AtUtc), ct).ConfigureAwait(false);
        LogPurged(logger, tenantId, report.TotalRows, users);
        return report;
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable) { throw new InvalidOperationException("No database is configured — there is no tenant data to export or purge."); }
    }

    internal sealed record TenantTable(uint Oid, string Schema, string Table)
    {
        public string Name => $"{Schema}.{Table}";
        public string Quoted => $"\"{Schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".\"{Table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static async Task<List<TenantTable>> TenantTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT c.oid, n.nspname, c.relname
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE a.attname = 'TenantId' AND c.relkind = 'r' AND NOT a.attisdropped
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            """;
        var list = new List<TenantTable>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new TenantTable(reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetString(2)));
        }
        return list;
    }

    private static async Task<List<(uint Child, uint Parent)>> ForeignKeysAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var edges = new List<(uint, uint)>();
        await using var cmd = new NpgsqlCommand("SELECT conrelid, confrelid FROM pg_constraint WHERE contype = 'f'", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            edges.Add((reader.GetFieldValue<uint>(0), reader.GetFieldValue<uint>(1)));
        }
        return edges;
    }

    /// <summary>Children before parents (Kahn's algorithm, O(V+E)); cycles and self-references fall back to name order.</summary>
    internal static List<TenantTable> DeleteOrder(IReadOnlyList<TenantTable> tables, IReadOnlyList<(uint Child, uint Parent)> foreignKeys)
    {
        var byOid = tables.ToDictionary(t => t.Oid);
        // A parent may be deleted once every child table referencing it is done.
        var pendingChildren = tables.ToDictionary(t => t.Oid, _ => 0);
        var parentsOf = tables.ToDictionary(t => t.Oid, _ => new List<uint>());
        foreach (var (child, parent) in foreignKeys.Distinct())
        {
            if (child == parent || !byOid.ContainsKey(child) || !byOid.ContainsKey(parent)) { continue; }
            pendingChildren[parent]++;
            parentsOf[child].Add(parent);
        }
        var ready = new SortedSet<string>(tables.Where(t => pendingChildren[t.Oid] == 0).Select(t => t.Name), StringComparer.Ordinal);
        var byName = tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var order = new List<TenantTable>(tables.Count);
        while (ready.Count > 0)
        {
            var next = byName[ready.Min!];
            ready.Remove(next.Name);
            order.Add(next);
            foreach (var parent in parentsOf[next.Oid])
            {
                pendingChildren[parent]--;
                if (pendingChildren[parent] == 0) { ready.Add(byOid[parent].Name); }
            }
        }
        // Anything left is in a cycle: append in name order (the transaction rolls back if the FK still bites).
        order.AddRange(tables.Where(t => !order.Contains(t)).OrderBy(t => t.Name, StringComparer.Ordinal));
        return order;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant {TenantId} PURGED: {Rows} rows, {Users} Keycloak users.")]
    private static partial void LogPurged(ILogger logger, string tenantId, long rows, int users);
}
