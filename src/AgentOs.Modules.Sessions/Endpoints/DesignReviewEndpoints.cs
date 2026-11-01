// Design gate (ADR-0016) — REST for a task's design reviews and their screenshots:
//   GET /tasks/{sessionId}/design                         → the task's design reviews, newest run first (no bytes)
//   GET /tasks/{sessionId}/design/{run}/shots/{**path}     → one screenshot (image/png, image/jpeg or image/webp)
// Board › Tasks' Design panel loads its screenshot grid from the second route (plain <img> tags — the bytes never ride
// the Blazor circuit). Auth: Member policy; the tenant comes from the token or cookie (ITenantContext) and every read is
// tenant-explicit, so one tenant can never see another's screenshots. A file is served only when (1) its path is one of
// the screenshots the review of that run lists, (2) it has an image extension (png / jpg / jpeg / webp — never SVG or
// HTML) and (3) its first bytes really are that image; it goes out inline with that image type and nosniff.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Domain.Sessions;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentOs.Modules.Sessions.Endpoints;

/// <summary>REST routes of the Design gate.</summary>
public static class DesignReviewEndpoints
{
    /// <summary>The route prefix of a task's design reviews.</summary>
    public const string Route = "/tasks/{sessionId:guid}/design";

    /// <summary>The URL of one screenshot (each path segment escaped); <paramref name="version"/> busts the browser cache
    /// when a re-test replaces the run's screenshots.</summary>
    public static string ShotUrl(Guid sessionId, int run, string path, long version) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"/tasks/{sessionId:D}/design/{run}/shots/{string.Join('/', (path ?? string.Empty).Split('/').Select(Uri.EscapeDataString))}?v={version}");

    /// <summary>Map the routes (the Api via the module; the Web host opts in by hand — Board › Tasks loads the grid from it).</summary>
    public static void MapDesignReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var design = endpoints.MapGroup(Route).RequireAuthorization("Member");
        design.MapGet(string.Empty, ListAsync);
        design.MapGet("/{run:int}/shots/{**path}", ShotAsync);
    }

    /// <summary>A design review as the API returns it.</summary>
    internal sealed record DesignReviewDto(int Run, int IssueNumber, string Status, string Gate, string Summary,
        System.Collections.Generic.IReadOnlyList<DesignFinding> Findings, System.Collections.Generic.IReadOnlyList<DesignShot> Shots,
        string? Decision, string? DecisionNote, string? DecidedBy, DateTimeOffset? DecidedAtUtc, DateTimeOffset UpdatedAtUtc);

    private static async Task<IResult> ListAsync(Guid sessionId, IDesignReviewStore reviews, ITenantContext tenant, CancellationToken ct)
    {
        var forTask = await reviews.ListForTaskAsync(tenant.TenantId, sessionId, ct).ConfigureAwait(false);
        return Results.Ok(forTask
            .Select(r => new DesignReviewDto(r.Run, r.IssueNumber, r.Status.ToString(), r.Gate.ToString(), r.Summary, r.Findings, r.Shots,
                r.Decision?.ToString(), r.DecisionNote, r.DecidedBy, r.DecidedAtUtc, r.UpdatedAtUtc))
            .ToList());
    }

    private static async Task<IResult> ShotAsync(Guid sessionId, int run, string path, HttpContext http, IDesignReviewStore reviews,
        IScopedFileStore files, ITenantContext tenant, CancellationToken ct)
    {
        if (!RunUploadRules.TryCheck(path, out var normalized, out _) || RunUploadRules.ContentType(normalized) is not { } type)
        {
            return Results.NotFound();
        }
        var review = await reviews.GetAsync(tenant.TenantId, sessionId, run, ct).ConfigureAwait(false);
        if (review is null || !review.Shots.Any(s => string.Equals(s.Path, normalized, StringComparison.OrdinalIgnoreCase))) { return Results.NotFound(); }
        var file = await files.GetAsync(tenant.TenantId, review.ScopeKey, normalized, ct).ConfigureAwait(false);
        if (file is null || !RunUploadRules.LooksLikeImage(normalized, file.Content)) { return Results.NotFound(); }
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(file.Content, type);
    }
}
