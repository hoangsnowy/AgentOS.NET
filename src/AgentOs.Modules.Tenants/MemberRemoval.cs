// GAP-04 — removing a member from a tenant, in the one order that can't leave their computers paired:
//   1. revoke (and disconnect) every runner they own in the tenant — idempotent, so a failure here removes nothing and a
//      retry starts over;
//   2. delete their Keycloak account;
//   3. once the delete is committed, finish with CancellationToken.None: a second revoke sweep (a computer paired between
//      1 and 2) and the member.removed audit row. A failure there is logged and recorded in the audit row, never thrown —
//      the member IS gone, and the 30 s revalidation backstop still cuts off anything left.
// The old order (delete, then revoke with the request token) left the runners live forever when anything failed after
// the delete: a retry got 404 from Keycloak. Shared by the Api (DELETE /tenants/{t}/members/{u}) and the Users app.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.SharedKernel.Logging;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Tenants;

/// <summary>How far a member removal got.</summary>
public enum MemberRemovalOutcome
{
    /// <summary>The account was deleted (and the audit row written). Check <see cref="MemberRemovalResult.Warning"/>.</summary>
    Removed,

    /// <summary>Their computers could not be revoked, so nothing was removed. Safe to retry.</summary>
    ComputersNotRevoked,

    /// <summary>Their computers were revoked, but deleting the account failed. Safe to retry.</summary>
    AccountNotDeleted,
}

/// <summary>The result of <see cref="MemberRemoval.RemoveAsync"/>.</summary>
/// <param name="Outcome">How far it got.</param>
/// <param name="ComputersRevoked">How many of the member's paired computers were revoked.</param>
/// <param name="Error">Why it stopped (not <see cref="MemberRemovalOutcome.Removed"/>), for the caller to show.</param>
/// <param name="Warning">Removed, but finishing up partly failed (in plain words).</param>
public sealed record MemberRemovalResult(MemberRemovalOutcome Outcome, int ComputersRevoked, string? Error = null, string? Warning = null)
{
    /// <summary>True when the account is gone.</summary>
    public bool Removed => Outcome == MemberRemovalOutcome.Removed;
}

/// <summary>Who removes the member, for the audit row.</summary>
public sealed record MemberRemovalActor(string? UserId, string? IpAddress = null, string? UserAgent = null);

/// <summary>Removes a member: revoke their computers, delete the account, audit (see file header).</summary>
public static partial class MemberRemoval
{
    /// <summary>What the remover is told when the account is gone but a computer may still be paired.</summary>
    public const string RevokeAfterDeleteFailed =
        "The member was removed, but disconnecting a computer they paired at the last moment failed. It is cut off within 30 seconds.";

    /// <summary>Remove <paramref name="userId"/> from <paramref name="tenantId"/>. Never throws for a failed step (the
    /// result says which); cancellation before the account is deleted propagates.</summary>
    public static async Task<MemberRemovalResult> RemoveAsync(
        string tenantId, string userId, MemberRemovalActor actor,
        IKeycloakAdminClient keycloak, IAuditLog audit, IRunnerDirectory? runners, IRunnerDisconnector? disconnector,
        ILogger? logger = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(keycloak);
        ArgumentNullException.ThrowIfNull(audit);

        // 1. Their computers first: if this fails, the account still exists and the remover can simply retry.
        IReadOnlyList<Guid> revoked;
        try
        {
            revoked = runners is null ? [] : await RunnerRevocation.RevokeMemberAsync(runners, disconnector, tenantId, userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger is not null) { LogRevokeFailed(logger, ex, LogSafe.Scrub(tenantId), LogSafe.Scrub(userId)); }
            return new MemberRemovalResult(MemberRemovalOutcome.ComputersNotRevoked, 0,
                $"Couldn't disconnect their paired computers, so nothing was removed: {ex.Message}");
        }

        // 2. The account.
        try
        {
            await keycloak.DeleteUserAsync(userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger is not null) { LogDeleteFailed(logger, ex, LogSafe.Scrub(tenantId), LogSafe.Scrub(userId)); }
            return new MemberRemovalResult(MemberRemovalOutcome.AccountNotDeleted, revoked.Count, ex.Message);
        }

        // 3. Committed — finish regardless of the caller going away.
        var count = revoked.Count;
        string? warning = null;
        if (runners is not null)
        {
            try
            {
                count += (await RunnerRevocation.RevokeMemberAsync(runners, disconnector, tenantId, userId, CancellationToken.None).ConfigureAwait(false)).Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger is not null) { LogRevokeFailed(logger, ex, LogSafe.Scrub(tenantId), LogSafe.Scrub(userId)); }
                warning = RevokeAfterDeleteFailed;
            }
        }
        var target = count == 0 ? $"user={userId}" : $"user={userId},runnersRevoked={count}";
        if (warning is not null) { target += ",runnerRevokeFailed=true"; }
        await audit.WriteAsync(new AuditEntry(
            Guid.NewGuid(), tenantId, actor.UserId, AuditActions.MemberRemoved, target,
            actor.IpAddress, actor.UserAgent, DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
        return new MemberRemovalResult(MemberRemovalOutcome.Removed, count, Warning: warning);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revoking the computers of member {UserId} in tenant {TenantId} failed.")]
    private static partial void LogRevokeFailed(ILogger logger, Exception ex, string tenantId, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting member {UserId} of tenant {TenantId} failed; their computers are already revoked.")]
    private static partial void LogDeleteFailed(ILogger logger, Exception ex, string tenantId, string userId);
}
