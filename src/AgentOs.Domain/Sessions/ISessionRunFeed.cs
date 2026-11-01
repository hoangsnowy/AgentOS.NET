// M6 — live session-run event feed. An in-memory, per-tenant pub/sub the desktop subscribes to so a
// running session's progress (dispatch → per-repo lifecycle → done/failed) surfaces live, replacing the
// old fixed-interval status poll. Best-effort by design: events are a UX nicety, NOT the source of
// truth — the session repository remains authoritative, so a dropped event or a subscriber that joined
// mid-run is reconciled by the next DB read (the desktop refreshes on every terminal event + a slow
// backstop poll). Pure BCL, so the contract lives in Domain next to the rest of the session model.

using System;
using System.Collections.Generic;
using System.Threading;

namespace AgentOs.Domain.Sessions;

/// <summary>What a <see cref="SessionRunEvent"/> represents.</summary>
public enum SessionRunEventKind
{
    /// <summary>The session run was dispatched to the runner.</summary>
    Running,

    /// <summary>Work started on one target repo.</summary>
    RepoStarted,

    /// <summary>A granular step within a repo (e.g. a pushed branch, an agent note).</summary>
    Step,

    /// <summary>One repo finished — branch pushed + PR opened, or failed.</summary>
    RepoDone,

    /// <summary>The whole session finished successfully.</summary>
    Done,

    /// <summary>The whole session failed.</summary>
    Failed,

    /// <summary>A run ended without its JSON reply and the agent asked it once for it — the message says whether that
    /// recovered the reply.</summary>
    ReplyRecovered,
}

/// <summary>One live event in a session run.</summary>
/// <param name="TenantId">Owning tenant — the partition key for fan-out.</param>
/// <param name="SessionId">The session this event belongs to.</param>
/// <param name="Kind">What the event represents.</param>
/// <param name="Message">Human-readable line for the activity feed.</param>
/// <param name="At">When the event occurred (UTC).</param>
/// <param name="Repo">Owner/name of the repo the event concerns, when applicable.</param>
/// <param name="Status">Resulting status for a <see cref="SessionRunEventKind.RepoDone"/> event.</param>
/// <param name="PrUrl">Opened PR url for a <see cref="SessionRunEventKind.RepoDone"/> event.</param>
public sealed record SessionRunEvent(
    string TenantId,
    Guid SessionId,
    SessionRunEventKind Kind,
    string Message,
    DateTimeOffset At,
    string? Repo = null,
    string? Status = null,
    string? PrUrl = null);

/// <summary>In-memory pub/sub for live session-run events, partitioned by tenant. Registered as a singleton.</summary>
public interface ISessionRunFeed
{
    /// <summary>Publish an event to every live subscriber of the event's tenant. Never throws, never blocks.</summary>
    void Publish(SessionRunEvent evt);

    /// <summary>Stream the live events for a tenant until <paramref name="ct"/> is cancelled. Each call is an
    /// independent subscription; events published before the subscription begins are not replayed.</summary>
    IAsyncEnumerable<SessionRunEvent> Subscribe(string tenantId, CancellationToken ct);
}
