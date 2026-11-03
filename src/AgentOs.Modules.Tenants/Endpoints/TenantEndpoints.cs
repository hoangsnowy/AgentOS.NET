// Tenant identity + admin endpoints. /tenants/me is open to any authenticated user (returns the
// resolved context); /tenants list + create + member invite require the Admin policy and reach
// into Keycloak via IKeycloakAdminClient to provision realm users.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants.Email;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentOs.Modules.Tenants.Endpoints;

internal static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/tenants/me", (ITenantContext tenant) => Results.Ok(new TenantMeResponse(
            tenant.TenantId,
            tenant.UserId,
            tenant.UserName,
            tenant.Roles,
            tenant.IsAuthenticated,
            tenant.IsAdmin)))
        .WithName("TenantMe")
        .WithSummary("Resolved tenant + user + roles for the current request")
        .WithTags("Tenants")
        .RequireAuthorization();

        app.MapGet("/tenants", async (ITenantsRepository repo, CancellationToken ct) =>
        {
            var list = await repo.ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(list);
        })
        .WithName("TenantsList")
        .WithSummary("List every tenant (platform admin only)")
        .WithTags("Tenants")
        // Cross-tenant by nature: the tenant 'admin' role is per tenant, so a tenant admin must never see
        // the platform's tenant list.
        .RequireAuthorization("PlatformAdmin");

        app.MapPost("/tenants", async (
            CreateTenantRequest body,
            ITenantsRepository repo,
            IKeycloakAdminClient kc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Id) || string.IsNullOrWhiteSpace(body.AdminUsername))
            {
                return Results.BadRequest(new { error = "id and adminUsername are required" });
            }

            var existing = await repo.GetAsync(body.Id, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                return Results.Conflict(new { error = $"Tenant '{body.Id}' already exists" });
            }

            var record = new TenantRecord(body.Id, string.IsNullOrWhiteSpace(body.Name) ? body.Id : body.Name, DateTimeOffset.UtcNow);
            await repo.AddAsync(record, ct).ConfigureAwait(false);

            string keycloakUserId;
            try
            {
                keycloakUserId = await kc.CreateUserAsync(
                    username: body.AdminUsername,
                    email: body.AdminEmail ?? string.Empty,
                    tenantId: body.Id,
                    realmRoles: AdminRole,
                    sendVerifyEmail: !string.IsNullOrWhiteSpace(body.AdminEmail),
                    ct: ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Keycloak provisioning failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Created($"/tenants/{body.Id}", new CreatedTenantResponse(record, keycloakUserId));
        })
        .WithName("TenantsCreate")
        .WithSummary("Provision a tenant + admin user (platform admin only)")
        .WithTags("Tenants")
        // Creating tenants (and their admin users) is a platform operation, not a tenant one.
        .RequireAuthorization("PlatformAdmin");

        // Self-service sign-up: dispatches to ITenantSignupService which picks one of three modes
        // (invite / slug / auto-create) by request shape. Creating a NEW workspace needs the platform setting
        // Tenants:OpenSignup (off outside Development — GAP-01); an invitation always works.
        app.MapPost("/tenants/register", async (
            RegisterTenantRequest body,
            ITenantSignupService signup,
            CancellationToken ct) =>
        {
            try
            {
                var outcome = await signup.SignupAsync(new TenantSignupRequest(
                    Username: body.Username,
                    Password: body.Password,
                    Email: body.Email,
                    TenantId: body.TenantId,
                    TenantName: body.TenantName,
                    InviteToken: body.InviteToken), ct).ConfigureAwait(false);
                return Results.Created(
                    $"/tenants/{outcome.TenantId}",
                    new RegisteredTenantResponse(outcome.TenantId, outcome.KeycloakUserId, outcome.Mode.ToString()));
            }
            catch (SignupClosedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("TenantsRegister")
        .WithSummary("Self-service sign-up: invite / slug / auto-create (public)")
        .WithTags("Tenants")
        .AllowAnonymous();

        // Admin-only: create an invitation (GAP-11). The returned `url` (signup page + ?invite=) is what the inviter
        // pastes into an email or chat. The invitation is single-use, bound to `email` (required), revocable until it is
        // used (POST …/invitations/{id}/revoke), and expires after `ttlHours` (1–168, default 24).
        app.MapPost("/tenants/{tenantId}/invitations", async (
            string tenantId,
            CreateInvitationRequest body,
            ITenantSignupService signup,
            IEmailSender email,
            ITenantContext ctx,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Tenant-scoped: an Admin can only mint invitations for their OWN tenant (the realm `admin`
            // role is realm-wide, so without this an admin of tenant A could provision into tenant B).
            if (!string.Equals(ctx.TenantId, tenantId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }
            var role = string.IsNullOrWhiteSpace(body.Role) ? "member" : body.Role;
            if (role is not ("admin" or "member"))
            {
                return Results.BadRequest(new { error = "role must be 'admin' or 'member'" });
            }
            // GAP-11: an invitation is bound to the address it is sent to — no address, no invitation.
            if (string.IsNullOrWhiteSpace(body.Email))
            {
                return Results.BadRequest(new { error = "email is required: an invitation can only be accepted by the address it was sent to" });
            }
            var ttl = TimeSpan.FromHours(body.TtlHours is > 0 and <= 168 ? body.TtlHours.Value : 24);
            InvitationToken minted;
            try
            {
                // Audited by the service (invitation.minted).
                minted = await signup.CreateInvitationAsync(tenantId, role, body.Email, ttl,
                    new AuditActor(ctx.UserId, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString()),
                    ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            var url = $"{origin}/signup?invite={Uri.EscapeDataString(minted.Token)}";

            // Best-effort: email the link to the invited address. The URL is returned regardless so the admin can still
            // copy/paste it if delivery is unconfigured or fails.
            var emailed = false;
            // Already logged by the sender; fall back to the returned URL. The address may also be
            // unparseable (MimeKit.ParseException) — same outcome: emailed stays false.
            static void Handle(Exception e) => _ = e.Message;
            try
            {
                var (subject, htmlBody, textBody) = InvitationEmail.Build(tenantId, role, url);
                await email.SendAsync(body.Email.Trim(), subject, htmlBody, textBody, ct).ConfigureAwait(false);
                emailed = true;
            }
            // ServiceNotConnectedException derives from InvalidOperationException; CommandException →
            // ProtocolException — derived clauses precede their bases (CS0160).
            catch (MailKit.ServiceNotConnectedException ex) { Handle(ex); }
            catch (MailKit.CommandException ex) { Handle(ex); }
            catch (MailKit.ProtocolException ex) { Handle(ex); }
            catch (MailKit.Security.AuthenticationException ex) { Handle(ex); }
            catch (System.Net.Sockets.SocketException ex) { Handle(ex); }
            catch (System.IO.IOException ex) { Handle(ex); }
            catch (InvalidOperationException ex) { Handle(ex); }
            catch (MimeKit.ParseException ex) { Handle(ex); }
            return Results.Ok(new CreatedInvitationResponse(minted.Token, url, minted.ExpiresAtUtc, emailed, minted.Id));
        })
        .WithName("TenantsCreateInvitation")
        .WithSummary("Create a single-use, email-bound invitation URL for the tenant (admin only)")
        .WithTags("Tenants")
        .RequireAuthorization("Admin");

        // The tenant's invitations, newest first, with their state (pending / accepted / revoked / expired).
        app.MapGet("/tenants/{tenantId}/invitations", async (
            string tenantId,
            ITenantSignupService signup,
            ITenantContext ctx,
            int? max,
            CancellationToken ct) =>
        {
            if (!string.Equals(ctx.TenantId, tenantId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }
            return Results.Ok(await signup.ListInvitationsAsync(tenantId, Math.Clamp(max ?? 100, 1, 500), ct).ConfigureAwait(false));
        })
        .WithName("TenantsListInvitations")
        .WithSummary("List the tenant's invitations and their state (tenant Admin only)")
        .WithTags("Tenants")
        .RequireAuthorization("Admin");

        // Withdraw an unused invitation (GAP-11): its link stops working at once. 409 when it was already used or revoked.
        app.MapPost("/tenants/{tenantId}/invitations/{invitationId:guid}/revoke", async (
            string tenantId,
            Guid invitationId,
            ITenantSignupService signup,
            ITenantContext ctx,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!string.Equals(ctx.TenantId, tenantId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }
            var actor = new AuditActor(ctx.UserId, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
            if (await signup.RevokeInvitationAsync(tenantId, invitationId, actor, ct).ConfigureAwait(false))
            {
                return Results.NoContent();
            }
            var known = (await signup.ListInvitationsAsync(tenantId, 500, ct).ConfigureAwait(false)).Any(i => i.Id == invitationId);
            return known
                ? Results.Conflict(new { error = "Only an invitation that has not been used or revoked can be revoked." })
                : Results.NotFound();
        })
        .WithName("TenantsRevokeInvitation")
        .WithSummary("Revoke an unused invitation (tenant Admin only)")
        .WithTags("Tenants")
        .RequireAuthorization("Admin");

        // Public preview: read an invitation link (no side effects). Lets the signup page show the tenant, role and the
        // (bound) email — or why the link can't be used (status accepted / revoked / expired).
        app.MapGet("/tenants/invitations/preview", async (
            string token,
            ITenantSignupService signup,
            CancellationToken ct) =>
        {
            var preview = await signup.PreviewInvitationAsync(token, ct).ConfigureAwait(false);
            return preview is null
                ? Results.NotFound(new { error = "Invitation is invalid" })
                : Results.Ok(preview);
        })
        .WithName("TenantsPreviewInvitation")
        .WithSummary("Decode an invitation token without consuming it (public)")
        .WithTags("Tenants")
        .AllowAnonymous();

        // List members of a tenant — only Admins, scoped to their own tenant by ITenantContext.
        app.MapGet("/tenants/{tenantId}/members", async (
            string tenantId,
            IKeycloakAdminClient kc,
            ITenantContext ctx,
            CancellationToken ct) =>
        {
            if (!string.Equals(ctx.TenantId, tenantId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }
            var members = await kc.ListUsersByTenantAsync(tenantId, max: 200, ct: ct).ConfigureAwait(false);
            return Results.Ok(members);
        })
        .WithName("TenantsListMembers")
        .WithSummary("List the members of the current tenant (tenant Admin only)")
        .WithTags("Tenants")
        .RequireAuthorization("Admin");

        // Change a member's roles (admin/member). Tenant-scoped: an Admin can only touch members of
        // their own tenant, and cannot strip their own last admin role (lock-out guard).
        app.MapPatch("/tenants/{tenantId}/members/{userId}", async (
            string tenantId,
            string userId,
            UpdateMemberRolesRequest body,
            IKeycloakAdminClient kc,
            IAuditLog audit,
            ITenantContext ctx,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!string.Equals(ctx.TenantId, tenantId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }
            var roles = (body.Roles ?? Array.Empty<string>())
                .Where(r => r is "admin" or "member")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (roles.Count == 0)
            {
                return Results.BadRequest(new { error = "roles must contain at least one of: admin, member" });
            }
            // Lock-out guard: an Admin editing themselves must keep the admin role.
            if (string.Equals(ctx.UserId, userId, StringComparison.Ordinal) && !roles.Contains("admin", StringComparer.Ordinal))
            {
                return Results.BadRequest(new { error = "You cannot remove your own admin role." });
            }
            try
            {
                if (!await UserInTenantAsync(kc, userId, tenantId, ct).ConfigureAwait(false))
                {
                    return Results.NotFound();
                }
                await kc.UpdateUserRolesAsync(userId, roles, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(title: "Keycloak role update failed", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            await audit.WriteAsync(new AuditEntry(
                Guid.NewGuid(), tenantId, ctx.UserId, AuditActions.MemberRoleChanged,
                Target: $"user={userId},roles={string.Join('+', roles)}",
                IpAddress: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                TimestampUtc: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            return Results.Ok(new { userId, roles });
        })
        .WithName("TenantsUpdateMemberRoles")
        .WithSummary("Change a member's roles (tenant Admin only)")
        .WithTags("Tenants")
        .RequireAuthorization("Admin");

        // Remove a member from the tenant (deletes the Keycloak user). Cannot remove yourself. Their paired computers are
        // revoked and disconnected too (GAP-04) — a departed member's runner must not keep taking (or answering) work.
        app.MapDelete("/tenants/{tenantId}/members/{userId}", RemoveMemberAsync)
        .WithName("TenantsRemoveMember")
        .WithSummary("Remove a member from the tenant (tenant Admin only)")
        .WithTags("Tenants")
        .RequireAuthorization("Admin");

        // Trigger a password-reset action email for a member (Keycloak owns the new password).
        app.MapPost("/tenants/{tenantId}/members/{userId}/reset-password", async (
            string tenantId,
            string userId,
            IKeycloakAdminClient kc,
            IAuditLog audit,
            ITenantContext ctx,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!string.Equals(ctx.TenantId, tenantId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }
            try
            {
                if (!await UserInTenantAsync(kc, userId, tenantId, ct).ConfigureAwait(false))
                {
                    return Results.NotFound();
                }
                await kc.SendPasswordResetEmailAsync(userId, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(title: "Keycloak password-reset failed", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            await audit.WriteAsync(new AuditEntry(
                Guid.NewGuid(), tenantId, ctx.UserId, AuditActions.MemberPasswordReset,
                Target: $"user={userId}",
                IpAddress: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                TimestampUtc: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            return Results.Accepted();
        })
        .WithName("TenantsResetMemberPassword")
        .WithSummary("Send a member a password-reset email (tenant Admin only)")
        .WithTags("Tenants")
        .RequireAuthorization("Admin");

        app.MapGet("/tenants/{tenantId}/audit", async (
            string tenantId,
            IAuditLog audit,
            ITenantContext ctx,
            int? max,
            CancellationToken ct) =>
        {
            if (!string.Equals(ctx.TenantId, tenantId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }
            var rows = await audit.ListAsync(tenantId, max ?? 100, ct).ConfigureAwait(false);
            return Results.Ok(rows);
        })
        .WithName("TenantsAudit")
        .WithSummary("Audit trail for the current tenant (tenant Admin only)")
        .WithTags("Tenants")
        .RequireAuthorization("Admin");

        // GAP-11b: walk the tenant's audit hash chain — intact, or the first event that was changed or removed.
        app.MapGet("/tenants/{tenantId}/audit/verify", async (
            string tenantId,
            [Microsoft.AspNetCore.Mvc.FromServices] IAuditChainVerifier verifier,
            ITenantContext ctx,
            CancellationToken ct) =>
        {
            if (!string.Equals(ctx.TenantId, tenantId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }
            var report = await verifier.VerifyAsync(tenantId, ct).ConfigureAwait(false);
            return Results.Ok(new AuditChainResponse(report.TenantId, report.Intact, report.Verified, report.Unchained,
                report.HeadHash, report.BrokenAt, report.BrokenEventId, report.Problem.ToString(), report.Summary));
        })
        .WithName("TenantsAuditVerify")
        .WithSummary("Verify the tenant's tamper-evident audit chain (tenant Admin only)")
        .WithTags("Tenants")
        .RequireAuthorization("Admin");

        app.MapPost("/tenants/{tenantId}/members", async (
            string tenantId,
            InviteMemberRequest body,
            ITenantsRepository repo,
            IKeycloakAdminClient kc,
            IAuditLog audit,
            ITenantContext ctx,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!string.Equals(ctx.TenantId, tenantId, StringComparison.Ordinal))
            {
                return Results.Forbid();
            }
            if (string.IsNullOrWhiteSpace(body.Username))
            {
                return Results.BadRequest(new { error = "username is required" });
            }
            var existing = await repo.GetAsync(tenantId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found" });
            }
            var roles = body.Roles is { Count: > 0 }
                ? body.Roles.Where(r => r is "admin" or "member").Distinct(StringComparer.Ordinal).ToList()
                : new List<string> { "member" };
            try
            {
                var userId = await kc.CreateUserAsync(
                    username: body.Username,
                    email: body.Email ?? string.Empty,
                    tenantId: tenantId,
                    realmRoles: roles,
                    sendVerifyEmail: !string.IsNullOrWhiteSpace(body.Email),
                    ct: ct).ConfigureAwait(false);
                await audit.WriteAsync(new AuditEntry(
                    Guid.NewGuid(), tenantId, ctx.UserId, AuditActions.MemberInvited,
                    Target: $"user={body.Username},roles={string.Join('+', roles)}",
                    IpAddress: http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: http.Request.Headers.UserAgent.ToString(),
                    TimestampUtc: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
                return Results.Created($"/tenants/{tenantId}/members/{userId}", new InvitedMemberResponse(userId, body.Username, roles));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Keycloak provisioning failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        })
        .WithName("TenantsInviteMember")
        .WithSummary("Invite a member into a tenant (admin only)")
        .WithTags("Tenants")
        .RequireAuthorization("Admin");

        return app;
    }

    private static readonly string[] AdminRole = new[] { "admin" };

    /// <summary>DELETE /tenants/{tenantId}/members/{userId}: their paired computers are revoked FIRST, then the account
    /// is deleted, then the audit row is written (<see cref="MemberRemoval"/>, GAP-04). The runner seams are optional —
    /// a host without the Sessions / RemoteAgent modules has no computers to revoke.</summary>
    internal static async Task<IResult> RemoveMemberAsync(
        string tenantId,
        string userId,
        IKeycloakAdminClient kc,
        IAuditLog audit,
        ITenantContext ctx,
        HttpContext http,
        CancellationToken ct,
        [Microsoft.AspNetCore.Mvc.FromServices] AgentOs.Domain.Sessions.IRunnerDirectory? runners = null,
        [Microsoft.AspNetCore.Mvc.FromServices] AgentOs.Domain.Sessions.IRunnerDisconnector? disconnector = null,
        [Microsoft.AspNetCore.Mvc.FromServices] Microsoft.Extensions.Logging.ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(kc);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(http);
        if (!string.Equals(ctx.TenantId, tenantId, StringComparison.Ordinal))
        {
            return Results.Forbid();
        }
        if (string.Equals(ctx.UserId, userId, StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = "You cannot remove yourself." });
        }
        try
        {
            if (!await UserInTenantAsync(kc, userId, tenantId, ct).ConfigureAwait(false))
            {
                return Results.NotFound();
            }
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Keycloak lookup failed", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        var result = await MemberRemoval.RemoveAsync(tenantId, userId,
            new MemberRemovalActor(ctx.UserId, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString()),
            kc, audit, runners, disconnector, loggers?.CreateLogger(typeof(MemberRemoval).FullName!), ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            MemberRemovalOutcome.ComputersNotRevoked => Results.Problem(
                title: "Couldn't disconnect the member's computers", detail: result.Error, statusCode: StatusCodes.Status503ServiceUnavailable),
            MemberRemovalOutcome.AccountNotDeleted => Results.Problem(
                title: "Keycloak delete failed", detail: result.Error, statusCode: StatusCodes.Status502BadGateway),
            _ => Results.NoContent(),
        };
    }

    /// <summary>True when the target user's <c>tenant</c> attribute equals <paramref name="tenantId"/>.
    /// The realm <c>admin</c> role is realm-wide, so a tenant-route match alone does NOT authorize acting
    /// on an arbitrary user id — this confirms the target actually belongs to the caller's tenant before a
    /// re-role / delete / password-reset. Throws <see cref="InvalidOperationException"/> if Keycloak is
    /// unreachable (callers map it to 502, same as the mutating call).</summary>
    private static async Task<bool> UserInTenantAsync(
        IKeycloakAdminClient kc, string userId, string tenantId, CancellationToken ct)
    {
        var targetTenant = await kc.GetUserTenantAsync(userId, ct).ConfigureAwait(false);
        return string.Equals(targetTenant, tenantId, StringComparison.Ordinal);
    }
}

/// <summary>Response shape for GET /tenants/me.</summary>
public sealed record TenantMeResponse(
    string TenantId,
    string? UserId,
    string? UserName,
    IReadOnlyList<string> Roles,
    bool IsAuthenticated,
    bool IsAdmin);

/// <summary>Body for POST /tenants.</summary>
public sealed record CreateTenantRequest(string Id, string? Name, string AdminUsername, string? AdminEmail);

/// <summary>Response for POST /tenants.</summary>
public sealed record CreatedTenantResponse(TenantRecord Tenant, string KeycloakUserId);

/// <summary>Body for POST /tenants/{id}/members.</summary>
public sealed record InviteMemberRequest(string Username, string? Email, IReadOnlyList<string>? Roles);

/// <summary>Body for PATCH /tenants/{id}/members/{userId}.</summary>
public sealed record UpdateMemberRolesRequest(IReadOnlyList<string>? Roles);

/// <summary>Response for POST /tenants/{id}/members.</summary>
public sealed record InvitedMemberResponse(string KeycloakUserId, string Username, IReadOnlyList<string> Roles);

/// <summary>Body for POST /tenants/register. TenantId + InviteToken are both optional — the service
/// picks the mode (invite / slug / auto-create) from which fields are present.</summary>
public sealed record RegisterTenantRequest(
    string? TenantId,
    string? TenantName,
    string Username,
    string? Email,
    string Password,
    string? InviteToken);

/// <summary>Response for POST /tenants/register.</summary>
public sealed record RegisteredTenantResponse(string TenantId, string KeycloakUserId, string Mode);

/// <summary>Body for POST /tenants/{id}/invitations.</summary>
public sealed record CreateInvitationRequest(string? Email, string? Role, int? TtlHours);

/// <summary>Response for POST /tenants/{id}/invitations.</summary>
/// <param name="Emailed">True when the invitation link was emailed to the supplied address.</param>
/// <param name="Id">The invitation's id (revoke it with POST /tenants/{id}/invitations/{invitationId}/revoke).</param>
public sealed record CreatedInvitationResponse(string Token, string Url, DateTimeOffset ExpiresAtUtc, bool Emailed = false, Guid? Id = null);

/// <summary>Response for GET /tenants/{id}/audit/verify.</summary>
public sealed record AuditChainResponse(
    string TenantId, bool Intact, long Verified, long Unchained, string? HeadHash, long? BrokenAt, Guid? BrokenEventId, string Problem, string Summary);
