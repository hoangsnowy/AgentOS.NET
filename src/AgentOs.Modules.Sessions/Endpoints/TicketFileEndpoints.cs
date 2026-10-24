// Ticket files (ADR-0015) — REST, so a person can script what the Board's "Files for the AI" does:
//   GET    /workspaces/{boardId}/tickets/{issue}/files              → the ticket's files (metadata)
//   GET    /workspaces/{boardId}/tickets/{issue}/files/{**path}     → one file's bytes
//   PUT    /workspaces/{boardId}/tickets/{issue}/files/{**path}     → add or replace it (the request body IS the file)
//   DELETE /workspaces/{boardId}/tickets/{issue}/files/{**path}     → remove it
// Auth: Member policy; the tenant comes from the token (ITenantContext) and every call is tenant-explicit, so one
// tenant can never read or write another's files. Upload is a raw-body PUT, not a multipart form: a cross-site page
// can't send a PUT without a CORS preflight, so the cookie-authenticated Web host needs no antiforgery token for it.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace AgentOs.Modules.Sessions.Endpoints;

internal static class TicketFileEndpoints
{
    /// <summary>The route prefix of a ticket's files.</summary>
    internal const string Route = "/workspaces/{boardId:guid}/tickets/{issue:int}/files";

    public static void MapTicketFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var files = endpoints.MapGroup(Route).RequireAuthorization("Member");
        files.MapGet(string.Empty, ListAsync);
        files.MapGet("/{**path}", DownloadAsync);
        files.MapPut("/{**path}", UploadAsync);
        files.MapDelete("/{**path}", DeleteAsync);
    }

    /// <summary>A file as the API returns it (no content).</summary>
    internal sealed record TicketFileDto(string Path, long Size, string Sha256, string ContentType, string UploadedBy, DateTimeOffset UploadedAtUtc)
    {
        public static TicketFileDto From(StoredFileInfo f) => new(f.Path, f.Size, f.Sha256, f.ContentType, f.UploadedBy, f.UploadedAtUtc);
    }

    private static async Task<IResult> ListAsync(Guid boardId, int issue, ITicketFiles files, ITenantContext tenant, CancellationToken ct)
    {
        if (Scope(boardId, issue) is not { } scope) { return Results.NotFound(); }
        var rows = await files.ListAsync(tenant.TenantId, scope, ct).ConfigureAwait(false);
        return Results.Ok(rows.Select(TicketFileDto.From).ToList());
    }

    private static async Task<IResult> DownloadAsync(Guid boardId, int issue, string path, ITicketFiles files, ITenantContext tenant, CancellationToken ct)
    {
        if (Scope(boardId, issue) is not { } scope) { return Results.NotFound(); }
        var file = await files.GetAsync(tenant.TenantId, scope, path, ct).ConfigureAwait(false);
        // Always a download (attachment), never rendered inline: an uploaded .html or .svg must not run in our origin.
        return file is null
            ? Results.NotFound()
            : Results.File(file.Content, "application/octet-stream", Path.GetFileName(file.Info.Path));
    }

    private static async Task<IResult> UploadAsync(Guid boardId, int issue, string path, HttpRequest request, ITicketFiles files,
        ITenantContext tenant, CancellationToken ct)
    {
        if (Scope(boardId, issue) is not { } scope) { return Results.NotFound(); }
        if (!TicketFilePath.TryNormalize(path, out _, out var pathError))
        {
            return Results.ValidationProblem(new System.Collections.Generic.Dictionary<string, string[]> { ["path"] = [pathError ?? "Invalid path."] });
        }
        var limits = await files.LimitsAsync(tenant.TenantId, ct).ConfigureAwait(false);
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = limits.MaxFileBytes + 1;
        }
        if (request.ContentLength > limits.MaxFileBytes)
        {
            return Results.Problem($"Files can be at most {limits.MaxFileBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        var content = await ReadBoundedAsync(request.Body, limits.MaxFileBytes, ct).ConfigureAwait(false);
        if (content is null)
        {
            return Results.Problem($"Files can be at most {limits.MaxFileBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        var put = await files.PutAsync(tenant.TenantId, scope, path, content, request.ContentType,
            tenant.UserName ?? tenant.UserId ?? "api", ct).ConfigureAwait(false);
        return put.File is { } stored
            ? Results.Created($"{request.Path}", TicketFileDto.From(stored))
            : Results.ValidationProblem(new System.Collections.Generic.Dictionary<string, string[]> { ["file"] = [put.Error ?? "The file was refused."] });
    }

    private static async Task<IResult> DeleteAsync(Guid boardId, int issue, string path, ITicketFiles files, ITenantContext tenant, CancellationToken ct)
    {
        if (Scope(boardId, issue) is not { } scope) { return Results.NotFound(); }
        return await files.DeleteAsync(tenant.TenantId, scope, path, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
    }

    private static string? Scope(Guid boardId, int issue) =>
        boardId == Guid.Empty || issue <= 0 ? null : TicketFileScope.ForTicket(boardId, issue);

    // Read at most max bytes; null when the body is longer (never buffers more than max + one read).
    internal static async Task<byte[]?> ReadBoundedAsync(Stream body, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await body.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) { break; }
            if (buffer.Length + read > max) { return null; }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
