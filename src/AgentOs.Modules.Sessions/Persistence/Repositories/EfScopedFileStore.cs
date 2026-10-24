// Ticket files (ADR-0015) — EF Core store over sessions.ticket_files. Every query is tenant-explicit
// (IgnoreQueryFilters + a TenantId predicate): a Blazor circuit or a background run has no ambient HTTP tenant, so the
// context's filter would read the wrong one. RLS (the context's interceptor) is the second wall. Listing projects the
// metadata only — the content column is read just for a download or an agent run's delivery.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Modules.Sessions.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Sessions.Persistence.Repositories;

internal sealed class EfScopedFileStore(SessionsDbContext db) : IScopedFileStore
{
    public async Task<StoredFileInfo> PutAsync(StoredFileInfo info, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(info.TenantId))
        {
            throw new InvalidOperationException("A ticket file needs a tenant.");
        }
        // One file per path KEY (case-insensitive): "LOGO.PNG" after "Logo.png" replaces it, as the unique index demands.
        var key = TicketFilePath.Key(info.Path);
        var row = await db.TicketFiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.TenantId == info.TenantId && f.ScopeKey == info.ScopeKey && f.PathKey == key, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new TicketFileEntity { Id = info.Id == Guid.Empty ? Guid.NewGuid() : info.Id, TenantId = info.TenantId, ScopeKey = info.ScopeKey, PathKey = key };
            db.TicketFiles.Add(row);
        }
        row.Path = info.Path;
        row.Content = content;
        row.Size = content.LongLength;
        row.Sha256 = info.Sha256;
        row.ContentType = info.ContentType;
        row.UploadedBy = info.UploadedBy;
        row.UploadedAtUtc = info.UploadedAtUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToInfo(row);
    }

    public async Task<IReadOnlyList<StoredFileInfo>> ListAsync(string tenantId, string scopeKey, CancellationToken ct = default) =>
        await db.TicketFiles.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.ScopeKey == scopeKey)
            .OrderBy(f => f.Path)
            .Select(f => new StoredFileInfo(f.Id, f.TenantId, f.ScopeKey, f.Path, f.Size, f.Sha256, f.ContentType, f.UploadedBy, f.UploadedAtUtc))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<StoredFile?> GetAsync(string tenantId, string scopeKey, string path, CancellationToken ct = default)
    {
        var key = TicketFilePath.Key(path);
        var row = await db.TicketFiles.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.ScopeKey == scopeKey && f.PathKey == key, ct).ConfigureAwait(false);
        return row is null ? null : new StoredFile(ToInfo(row), row.Content);
    }

    public async Task<IReadOnlyList<StoredFile>> LoadAllAsync(string tenantId, string scopeKey, CancellationToken ct = default)
    {
        var rows = await db.TicketFiles.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.ScopeKey == scopeKey)
            .OrderBy(f => f.Path)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new StoredFile(ToInfo(r), r.Content)).ToList();
    }

    public async Task<bool> DeleteAsync(string tenantId, string scopeKey, string path, CancellationToken ct = default)
    {
        var key = TicketFilePath.Key(path);
        return await RemoveAsync(db.TicketFiles.IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.ScopeKey == scopeKey && f.PathKey == key), ct).ConfigureAwait(false) > 0;
    }

    public Task<int> DeleteScopeAsync(string tenantId, string scopeKey, CancellationToken ct = default) =>
        RemoveAsync(db.TicketFiles.IgnoreQueryFilters().Where(f => f.TenantId == tenantId && f.ScopeKey == scopeKey), ct);

    // Delete by key without reading the content column: the ids are read, then removed as key-only stubs (a row the
    // context already tracks is removed as itself).
    private async Task<int> RemoveAsync(IQueryable<TicketFileEntity> rows, CancellationToken ct)
    {
        var ids = await rows.Select(f => f.Id).ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0) { return 0; }
        foreach (var id in ids)
        {
            db.TicketFiles.Remove(db.TicketFiles.Local.FirstOrDefault(f => f.Id == id) ?? new TicketFileEntity { Id = id });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ids.Count;
    }

    private static StoredFileInfo ToInfo(TicketFileEntity f) =>
        new(f.Id, f.TenantId, f.ScopeKey, f.Path, f.Size, f.Sha256, f.ContentType, f.UploadedBy, f.UploadedAtUtc);
}
