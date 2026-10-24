// Ticket files (ADR-0015) — the rules around the store: every path normalised and checked (TicketFilePath), the
// tenant's limits applied (TicketFiles:* settings, read per call — UI-first, ADR-0011), SHA-256 recorded, content type
// kept as given (never trusted for anything but display). Files are data: nothing here or on the runner ever executes
// them. Singleton; every call opens its own scope (the store is a scoped EF repository) and is tenant-explicit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Files;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.Sessions.Files;

/// <inheritdoc cref="ITicketFiles"/>
internal sealed class TicketFiles(IServiceScopeFactory scopes, TimeProvider clock, ISettings? settings = null) : ITicketFiles
{
    private const string DefaultContentType = "application/octet-stream";

    public async Task<TicketFileLimits> LimitsAsync(string tenantId, CancellationToken ct = default) =>
        settings is null ? TicketFileLimits.Default : await TicketFileSettings.ReadAsync(settings, tenantId, ct).ConfigureAwait(false);

    public async Task<TicketFilePut> PutAsync(string tenantId, string scopeKey, string path, byte[] content, string? contentType,
        string uploadedBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(content);
        if (ScopeProblem(scopeKey) is { } scopeError) { return new TicketFilePut(null, scopeError); }
        if (!TicketFilePath.TryNormalize(path, out var normalized, out var pathError)) { return new TicketFilePut(null, pathError); }

        var limits = await LimitsAsync(tenantId, ct).ConfigureAwait(false);
        if (content.LongLength > limits.MaxFileBytes)
        {
            return new TicketFilePut(null, $"\"{normalized}\" is {Size(content.LongLength)} — files can be at most {Size(limits.MaxFileBytes)}.");
        }
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScopedFileStore>();
        var existing = await store.ListAsync(tenantId, scopeKey, ct).ConfigureAwait(false);
        var replaced = existing.FirstOrDefault(f => string.Equals(f.Path, normalized, StringComparison.Ordinal));
        // Windows (and macOS) treat "Logo.png" and "logo.png" as one file: two of them would collide on the runner.
        if (replaced is null && existing.FirstOrDefault(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase)) is { } twin)
        {
            return new TicketFilePut(null, $"\"{twin.Path}\" is already attached — a name that differs only in capitals would be the same file on most computers.");
        }
        if (replaced is null && existing.Count >= limits.MaxFiles)
        {
            return new TicketFilePut(null, $"This ticket already has {existing.Count} files — the most it can have is {limits.MaxFiles}.");
        }
        var total = existing.Sum(f => f.Size) - (replaced?.Size ?? 0) + content.LongLength;
        if (total > limits.MaxTotalBytes)
        {
            return new TicketFilePut(null, $"Adding \"{normalized}\" would make this ticket's files {Size(total)} — together they can be at most {Size(limits.MaxTotalBytes)}.");
        }
        var info = new StoredFileInfo(Guid.NewGuid(), tenantId, scopeKey, normalized, content.LongLength, RunFile.Hash(content),
            CleanContentType(contentType), Clip(uploadedBy, 256), clock.GetUtcNow());
        try
        {
            return new TicketFilePut(await store.PutAsync(info, content, ct).ConfigureAwait(false), null);
        }
        catch (InvalidOperationException ex)
        {
            return new TicketFilePut(null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<StoredFileInfo>> ListAsync(string tenantId, string scopeKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || ScopeProblem(scopeKey) is not null) { return []; }
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IScopedFileStore>().ListAsync(tenantId, scopeKey, ct).ConfigureAwait(false);
    }

    public async Task<StoredFile?> GetAsync(string tenantId, string scopeKey, string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || ScopeProblem(scopeKey) is not null || TicketFilePath.Normalize(path) is not { } normalized)
        {
            return null;
        }
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IScopedFileStore>().GetAsync(tenantId, scopeKey, normalized, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string tenantId, string scopeKey, string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || ScopeProblem(scopeKey) is not null || TicketFilePath.Normalize(path) is not { } normalized)
        {
            return false;
        }
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IScopedFileStore>().DeleteAsync(tenantId, scopeKey, normalized, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteScopeAsync(string tenantId, string scopeKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || ScopeProblem(scopeKey) is not null) { return 0; }
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IScopedFileStore>().DeleteScopeAsync(tenantId, scopeKey, ct).ConfigureAwait(false);
    }

    public async Task<int> CopyScopeAsync(string tenantId, string fromScope, string toScope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || ScopeProblem(fromScope) is not null || ScopeProblem(toScope) is not null
            || string.Equals(fromScope, toScope, StringComparison.Ordinal))
        {
            return 0;
        }
        IReadOnlyList<StoredFile> files;
        using (var scope = scopes.CreateScope())
        {
            files = await scope.ServiceProvider.GetRequiredService<IScopedFileStore>().LoadAllAsync(tenantId, fromScope, ct).ConfigureAwait(false);
        }
        var copied = 0;
        foreach (var f in files)
        {
            var put = await PutAsync(tenantId, toScope, f.Info.Path, f.Content, f.Info.ContentType, f.Info.UploadedBy, ct).ConfigureAwait(false);
            if (put.Ok) { copied++; }
        }
        return copied;
    }

    public async Task<IReadOnlyList<RunFile>> ForRunAsync(string tenantId, string scopeKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || ScopeProblem(scopeKey) is not null) { return []; }
        IReadOnlyList<StoredFile> files;
        using (var scope = scopes.CreateScope())
        {
            files = await scope.ServiceProvider.GetRequiredService<IScopedFileStore>().LoadAllAsync(tenantId, scopeKey, ct).ConfigureAwait(false);
        }
        // A run never carries more than the hard cap, whatever was stored before a limit was lowered: the files that fit,
        // in path order (a stored path that fails today's rules is skipped — the runner would refuse it anyway).
        var result = new List<RunFile>(files.Count);
        long total = 0;
        foreach (var f in files)
        {
            if (TicketFilePath.Normalize(f.Info.Path) is null || total + f.Content.LongLength > RunFile.MaxDeliveryBytes) { continue; }
            total += f.Content.LongLength;
            result.Add(new RunFile(f.Info.Path, f.Content, f.Info.Sha256, f.Info.ContentType));
        }
        return result;
    }

    /// <summary>"3.2 MB" — the size in the words the UI and the errors use.</summary>
    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    private static string? ScopeProblem(string? scopeKey) =>
        string.IsNullOrWhiteSpace(scopeKey) || scopeKey.Length > TicketFileScope.MaxLength || scopeKey.Any(char.IsControl)
            ? "This ticket can't hold files (no scope)."
            : null;

    // The browser's guess, kept for display; anything odd becomes the generic binary type.
    private static string CleanContentType(string? contentType)
    {
        var t = contentType?.Trim();
        return string.IsNullOrEmpty(t) || t.Length > 128 || t.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || !t.Contains('/', StringComparison.Ordinal)
            ? DefaultContentType
            : t;
    }

    private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "unknown" : s.Length <= max ? s : s[..max];
}
