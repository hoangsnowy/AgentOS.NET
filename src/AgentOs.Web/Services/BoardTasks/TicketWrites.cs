// E1.8 — what a run writes to its ticket, wherever the ticket lives. A GitHub Projects board keeps exactly the calls it
// always made (issue comments and closes through IPrCreationService; "Closes #N" in the pull request moves and links
// it). A board with an external tracker (Azure DevOps Boards / Jira) goes through the source-provider seam instead —
// the tracker routing sends the call to the tracker — and also gets the moves GitHub does by itself: Started when a run
// begins, Merged when its pull request is merged, and a link to each pull request. Every call is best-effort: a tracker
// hiccup is logged, never fails the run.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Web.Services.BoardTasks;

/// <summary>Comments, closes, moves and links on a task's ticket — on GitHub or on the board's tracker.</summary>
public static class TicketWrites
{
    /// <summary>The board's external tracker, or null for a GitHub Projects board.</summary>
    public static TrackerBinding? Tracker(WorkspaceEntity board) => board?.ReadTracker();

    /// <summary>Comment on ticket <paramref name="number"/>. Never throws.</summary>
    public static async Task<bool> CommentAsync(IServiceProvider sp, WorkspaceEntity board, WorkspaceDescriptor repo, int number, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(board);
        if (Tracker(board) is null)
        {
            return await sp.GetRequiredService<IPrCreationService>().CommentAsync(repo, number, body, ct).ConfigureAwait(false);
        }
        return await Safe(sp, board, number, "comment on", () => Provider(sp, board) is { } p
            ? p.CommentOnTicketAsync(repo, number, body, ct)
            : Task.FromResult(false)).ConfigureAwait(false);
    }

    /// <summary>Close ticket <paramref name="number"/> with <paramref name="comment"/>: a GitHub issue is closed; a tracker
    /// ticket gets the comment and is moved to the board's done state. Never throws.</summary>
    public static async Task<bool> CloseAsync(IServiceProvider sp, WorkspaceEntity board, WorkspaceDescriptor repo, int number, string comment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(board);
        if (Tracker(board) is null)
        {
            return await sp.GetRequiredService<IPrCreationService>().CloseAsync(repo, number, comment, ct).ConfigureAwait(false);
        }
        var commented = await CommentAsync(sp, board, repo, number, comment, ct).ConfigureAwait(false);
        var moved = await MoveAsync(sp, board, repo, number, TicketMilestone.Merged, ct).ConfigureAwait(false);
        return commented && moved;
    }

    /// <summary>Move a tracker ticket for <paramref name="milestone"/>; nothing to do on a GitHub board. Never throws.</summary>
    public static Task<bool> MoveAsync(IServiceProvider sp, WorkspaceEntity board, WorkspaceDescriptor repo, int? number, TicketMilestone milestone, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(board);
        if (Tracker(board) is null || number is not { } n || n <= 0) { return Task.FromResult(false); }
        return Safe(sp, board, n, milestone == TicketMilestone.Started ? "move (started)" : "move (merged)", () => Provider(sp, board) is { } p
            ? p.MoveTicketAsync(repo, n, milestone, ct)
            : Task.FromResult(false));
    }

    /// <summary>Link pull request <paramref name="url"/> to a tracker ticket; GitHub links it by itself. Never throws.</summary>
    public static Task<bool> LinkAsync(IServiceProvider sp, WorkspaceEntity board, WorkspaceDescriptor repo, int? number, string? url, string title, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(board);
        if (Tracker(board) is null || number is not { } n || n <= 0 || string.IsNullOrWhiteSpace(url)) { return Task.FromResult(false); }
        return Safe(sp, board, n, "link a pull request to", () => Provider(sp, board) is { } p
            ? p.LinkPullRequestAsync(repo, n, url, title, ct)
            : Task.FromResult(false));
    }

    /// <summary>Run <see cref="MoveAsync"/> in its own scope, off the caller's path (a UI click must not wait on Jira).</summary>
    public static void MoveInBackground(IServiceScopeFactory scopes, WorkspaceEntity board, WorkspaceDescriptor repo, int? number, TicketMilestone milestone)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (Tracker(board) is null || number is not > 0) { return; }
        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            await MoveAsync(scope.ServiceProvider, board, repo, number, milestone, CancellationToken.None).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    private static ISourceProvider? Provider(IServiceProvider sp, WorkspaceEntity board) =>
        sp.GetService<ISourceProviderResolver>() is { } resolver && resolver.TryResolve(board.Kind, out var p) ? p : null;

    private static async Task<bool> Safe(IServiceProvider sp, WorkspaceEntity board, int number, string what, Func<Task<bool>> call)
    {
        try
        {
            var ok = await call().ConfigureAwait(false);
            if (!ok)
            {
                sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(TicketWrites)).LogInformation(
                    "Couldn't {What} ticket {Number} on board {Board}'s tracker.", what, number, board.Id);
            }
            return ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(TicketWrites)).LogWarning(ex,
                "Couldn't {What} ticket {Number} on board {Board}'s tracker.", what, number, board.Id);
            return false;
        }
    }
}
