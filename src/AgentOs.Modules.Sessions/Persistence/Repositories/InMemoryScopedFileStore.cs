// Ticket files (ADR-0015) — the store of the no-database path (standalone Development, CI probes): a process-local,
// tenant-keyed map, so attaching files works end to end in `dotnet run --project src/AgentOs.Web` and its E2E. Lost on
// restart, by design. Keyed like the database's unique index — (tenant, scope, path KEY): names that differ only in
// capitals are one file. Bounded: past MaxBytes in total it refuses new files instead of growing without limit.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Files;

namespace AgentOs.Modules.Sessions.Persistence.Repositories;

internal sealed class InMemoryScopedFileStore : IScopedFileStore
{
    /// <summary>All files of every tenant together (bytes) — a Development store, not a production one.</summary>
    internal const long MaxBytes = 512L * 1024 * 1024;

    private readonly ConcurrentDictionary<(string Tenant, string Scope, string PathKey), StoredFile> _files = new();
    private readonly Lock _gate = new();
    private long _bytes;

    public Task<StoredFileInfo> PutAsync(StoredFileInfo info, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(content);
        var key = (info.TenantId, info.ScopeKey, TicketFilePath.Key(info.Path));
        lock (_gate)
        {
            var old = _files.TryGetValue(key, out var existing) ? existing : null;
            var previous = old?.Content.LongLength ?? 0;
            if (_bytes - previous + content.LongLength > MaxBytes)
            {
                throw new InvalidOperationException("The in-memory file store is full — restart the app or connect a database.");
            }
            var stored = info with { Id = old?.Info.Id ?? (info.Id == Guid.Empty ? Guid.NewGuid() : info.Id), Size = content.LongLength };
            _files[key] = new StoredFile(stored, content);
            _bytes += content.LongLength - previous;
            return Task.FromResult(stored);
        }
    }

    public Task<IReadOnlyList<StoredFileInfo>> ListAsync(string tenantId, string scopeKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredFileInfo>>(Scope(tenantId, scopeKey).Select(f => f.Info).ToList());

    public Task<StoredFile?> GetAsync(string tenantId, string scopeKey, string path, CancellationToken ct = default) =>
        Task.FromResult(_files.TryGetValue((tenantId, scopeKey, TicketFilePath.Key(path)), out var f) ? f : null);

    public Task<IReadOnlyList<StoredFile>> LoadAllAsync(string tenantId, string scopeKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredFile>>(Scope(tenantId, scopeKey).ToList());

    public Task<bool> DeleteAsync(string tenantId, string scopeKey, string path, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_files.TryRemove((tenantId, scopeKey, TicketFilePath.Key(path)), out var removed)) { return Task.FromResult(false); }
            _bytes -= removed.Content.LongLength;
            return Task.FromResult(true);
        }
    }

    public Task<int> DeleteScopeAsync(string tenantId, string scopeKey, CancellationToken ct = default)
    {
        // Every write takes the gate, so what is listed under it is exactly what gets removed.
        lock (_gate)
        {
            var doomed = _files.Where(kv => kv.Key.Tenant == tenantId && kv.Key.Scope == scopeKey).ToList();
            foreach (var (key, file) in doomed)
            {
                _files.TryRemove(key, out _);
                _bytes -= file.Content.LongLength;
            }
            return Task.FromResult(doomed.Count);
        }
    }

    private IEnumerable<StoredFile> Scope(string tenantId, string scopeKey) =>
        _files.Where(kv => kv.Key.Tenant == tenantId && kv.Key.Scope == scopeKey)
            .Select(kv => kv.Value)
            .OrderBy(f => f.Info.Path, StringComparer.Ordinal);
}
