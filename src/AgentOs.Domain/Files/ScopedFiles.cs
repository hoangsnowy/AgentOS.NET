// AgentOs.Domain/Files/ScopedFiles.cs
// Ticket files (ADR-0015): the only way files reach an agent. A person attaches files to a ticket in the desktop (or
// through the REST API); AgentOS.NET stores them per tenant under a SCOPE KEY, and every agent run for that ticket gets
// them in its working folder. The store is generic — Put / List / Get / Delete by tenant + scope key — so the next
// consumer (design-gate screenshots) reuses it with its own scope.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Files;

/// <summary>A stored file's metadata (never its content).</summary>
/// <param name="Path">The normalised relative path (<see cref="TicketFilePath"/>).</param>
/// <param name="Size">Bytes.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the content.</param>
public sealed record StoredFileInfo(
    Guid Id,
    string TenantId,
    string ScopeKey,
    string Path,
    long Size,
    string Sha256,
    string ContentType,
    string UploadedBy,
    DateTimeOffset UploadedAtUtc);

/// <summary>A stored file with its content.</summary>
public sealed record StoredFile(StoredFileInfo Info, byte[] Content);

/// <summary>
/// Tenant-scoped file storage keyed by (tenant, scope key, path). Every method is tenant-explicit (a Blazor circuit or
/// a background run has no ambient HTTP tenant). Paths reach the store already normalised and checked by
/// <see cref="ITicketFiles"/>; the store only persists. Putting an existing path replaces it.
/// </summary>
public interface IScopedFileStore
{
    /// <summary>Store (or replace) one file.</summary>
    Task<StoredFileInfo> PutAsync(StoredFileInfo info, byte[] content, CancellationToken ct = default);

    /// <summary>The files of a scope, by path (metadata only).</summary>
    Task<IReadOnlyList<StoredFileInfo>> ListAsync(string tenantId, string scopeKey, CancellationToken ct = default);

    /// <summary>One file with its content, or null.</summary>
    Task<StoredFile?> GetAsync(string tenantId, string scopeKey, string path, CancellationToken ct = default);

    /// <summary>Every file of a scope with its content (an agent run's delivery).</summary>
    Task<IReadOnlyList<StoredFile>> LoadAllAsync(string tenantId, string scopeKey, CancellationToken ct = default);

    /// <summary>Remove one file; false when there was none.</summary>
    Task<bool> DeleteAsync(string tenantId, string scopeKey, string path, CancellationToken ct = default);

    /// <summary>Remove every file of a scope; returns how many.</summary>
    Task<int> DeleteScopeAsync(string tenantId, string scopeKey, CancellationToken ct = default);
}

/// <summary>Scope keys of ticket files. A ticket is (board, issue number); a New idea draft has its own key until its
/// tickets exist.</summary>
public static class TicketFileScope
{
    /// <summary>The longest scope key (characters) — the column width.</summary>
    public const int MaxLength = 128;

    /// <summary>The files of ticket #<paramref name="issueNumber"/> on board <paramref name="boardId"/>.</summary>
    public static string ForTicket(Guid boardId, int issueNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"ticket:{boardId:N}:{issueNumber}");

    /// <summary>The files of a task that has no ticket number (an ad-hoc task).</summary>
    public static string ForSession(Guid sessionId) => string.Create(CultureInfo.InvariantCulture, $"session:{sessionId:N}");

    /// <summary>The files of a New idea draft, before its tickets are created.</summary>
    public static string ForDraft(Guid draftId) => string.Create(CultureInfo.InvariantCulture, $"draft:{draftId:N}");

    /// <summary>The scope a task's runs read: its ticket's when it has a number, else its own.</summary>
    public static string ForTask(Guid boardId, int? issueNumber, Guid sessionId) =>
        issueNumber is > 0 ? ForTicket(boardId, issueNumber.Value) : ForSession(sessionId);
}

/// <summary>The bounds on a scope's files (settings, per tenant — <c>TicketFiles:*</c>).</summary>
/// <param name="MaxFileBytes">The largest single file.</param>
/// <param name="MaxTotalBytes">All files of one ticket together.</param>
/// <param name="MaxFiles">How many files one ticket may have.</param>
public sealed record TicketFileLimits(long MaxFileBytes, long MaxTotalBytes, int MaxFiles)
{
    /// <summary>The built-in limits: 10 MB a file, 50 MB a ticket, 500 files.</summary>
    public static TicketFileLimits Default { get; } = new(10L * 1024 * 1024, 50L * 1024 * 1024, 500);
}

/// <summary>The outcome of adding a file: the stored file, or why it was refused (plain language).</summary>
public sealed record TicketFilePut(StoredFileInfo? File, string? Error)
{
    /// <summary>True when the file was stored.</summary>
    public bool Ok => File is not null;
}

/// <summary>
/// Ticket files with their rules: paths normalised and checked (<see cref="TicketFilePath"/>), the tenant's limits
/// applied, SHA-256 recorded. Files are data — never executed. Implemented by the Sessions module.
/// </summary>
public interface ITicketFiles
{
    /// <summary>The tenant's limits, read now.</summary>
    Task<TicketFileLimits> LimitsAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Add (or replace) a file. Never throws for a bad path or a limit — the result says why.</summary>
    Task<TicketFilePut> PutAsync(string tenantId, string scopeKey, string path, byte[] content, string? contentType,
        string uploadedBy, CancellationToken ct = default);

    /// <summary>The files of a scope (metadata).</summary>
    Task<IReadOnlyList<StoredFileInfo>> ListAsync(string tenantId, string scopeKey, CancellationToken ct = default);

    /// <summary>One file with its content, or null.</summary>
    Task<StoredFile?> GetAsync(string tenantId, string scopeKey, string path, CancellationToken ct = default);

    /// <summary>Remove one file.</summary>
    Task<bool> DeleteAsync(string tenantId, string scopeKey, string path, CancellationToken ct = default);

    /// <summary>Remove every file of a scope.</summary>
    Task<int> DeleteScopeAsync(string tenantId, string scopeKey, CancellationToken ct = default);

    /// <summary>Copy every file of <paramref name="fromScope"/> to <paramref name="toScope"/> (a draft's files onto the
    /// tickets created from it), applying the target's limits; returns how many were copied.</summary>
    Task<int> CopyScopeAsync(string tenantId, string fromScope, string toScope, CancellationToken ct = default);

    /// <summary>Every file of a scope, ready to send with an agent run.</summary>
    Task<IReadOnlyList<RunFile>> ForRunAsync(string tenantId, string scopeKey, CancellationToken ct = default);
}

/// <summary>A file sent with an agent run: written by the runner at <c>&lt;run dir&gt;/.agentos/files/&lt;Path&gt;</c>
/// before the agent starts.</summary>
public sealed record RunFile(string Path, byte[] Content, string Sha256, string ContentType)
{
    /// <summary>Bytes.</summary>
    public long Size => Content.LongLength;

    /// <summary>Where the runner puts the files, relative to the run's working folder.</summary>
    public const string Folder = ".agentos/files";

    /// <summary>The hard cap on what one run carries, whatever a tenant's limits say (bytes).</summary>
    public const long MaxDeliveryBytes = 512L * 1024 * 1024;

    /// <summary>Lower-case hex SHA-256 of <paramref name="content"/>.</summary>
    public static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
}

/// <summary>Where a delivery of ticket files to a member's computer landed (the server-side <c>runner_shell</c> path,
/// where the agent drives the computer through shell commands and needs the folder's absolute path).</summary>
public sealed record RunnerFileDelivery(bool Ok, string? Folder, string? Error);

/// <summary>Sends ticket files to the member's paired computer ahead of a server-driven run. Implemented by the
/// RemoteAgent module; every delivery is recorded as evidence.</summary>
public interface IRunnerFileDelivery
{
    /// <summary>Write <paramref name="files"/> into a fresh folder on the member's computer; the result names it.</summary>
    Task<RunnerFileDelivery> DeliverAsync(string tenantId, string memberId, IReadOnlyList<RunFile> files, CancellationToken ct = default);
}
