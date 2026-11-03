// GAP-11 — the rules an invitation obeys, as pure functions (unit-tested without a database or Keycloak):
//   • its state: Revoked (an admin withdrew it) > Accepted (used once — single-use) > Expired > Pending;
//   • acceptance: only a Pending invitation, and only for the address it was sent to — compared case-insensitively
//     with surrounding whitespace ignored (NormalizeEmail), so "Bob@Acme.test " accepts an invitation to bob@acme.test
//     and nothing else does;
//   • the plain-language reason a person sees when it can't be used.

using System;

namespace AgentOs.Modules.Tenants.Invitations;

/// <summary>Why an invitation can or cannot be accepted.</summary>
public enum InvitationAcceptance
{
    /// <summary>It can be accepted.</summary>
    Ok = 0,

    /// <summary>No such invitation (an unknown, tampered or pre-upgrade link).</summary>
    NotFound = 1,

    /// <summary>Past its expiry.</summary>
    Expired = 2,

    /// <summary>An administrator revoked it.</summary>
    Revoked = 3,

    /// <summary>It was already used (invitations are single-use).</summary>
    AlreadyUsed = 4,

    /// <summary>No email address was given.</summary>
    EmailRequired = 5,

    /// <summary>The address is not the one the invitation was sent to.</summary>
    EmailMismatch = 6,
}

/// <summary>The pure invitation rules (see the file header).</summary>
public static class InvitationRules
{
    /// <summary>Longest address accepted (RFC 5321 path limit is 256; 320 = 64 local + @ + 255 domain).</summary>
    public const int MaxEmailLength = 320;

    /// <summary>The form an address is compared in: trimmed and lower-cased (culture-invariant).</summary>
    public static string NormalizeEmail(string? email)
        => string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();

    /// <summary>Where an invitation stands at <paramref name="now"/>.</summary>
    public static InvitationStatus StatusOf(DateTimeOffset expiresAtUtc, DateTimeOffset? acceptedAtUtc, DateTimeOffset? revokedAtUtc, DateTimeOffset now)
    {
        if (revokedAtUtc is not null) { return InvitationStatus.Revoked; }
        if (acceptedAtUtc is not null) { return InvitationStatus.Accepted; }
        return now >= expiresAtUtc ? InvitationStatus.Expired : InvitationStatus.Pending;
    }

    /// <summary>May the person presenting <paramref name="presentedEmail"/> accept an invitation in <paramref name="status"/>
    /// sent to <paramref name="invitedEmailNormalized"/>? The state is checked first: a used invitation says "already
    /// used" whoever asks.</summary>
    public static InvitationAcceptance CheckAcceptance(InvitationStatus status, string invitedEmailNormalized, string? presentedEmail)
    {
        if (status != InvitationStatus.Pending) { return ForStatus(status); }
        var presented = NormalizeEmail(presentedEmail);
        if (presented.Length == 0) { return InvitationAcceptance.EmailRequired; }
        return string.Equals(presented, invitedEmailNormalized, StringComparison.Ordinal)
            ? InvitationAcceptance.Ok
            : InvitationAcceptance.EmailMismatch;
    }

    /// <summary>The acceptance outcome for a state alone (no address checked) — what a link preview shows.</summary>
    public static InvitationAcceptance ForStatus(InvitationStatus status) => status switch
    {
        InvitationStatus.Revoked => InvitationAcceptance.Revoked,
        InvitationStatus.Accepted => InvitationAcceptance.AlreadyUsed,
        InvitationStatus.Expired => InvitationAcceptance.Expired,
        _ => InvitationAcceptance.Ok,
    };

    /// <summary>What the invitee is told, in plain words.</summary>
    public static string Describe(InvitationAcceptance outcome) => outcome switch
    {
        InvitationAcceptance.Ok => "This invitation can be used.",
        InvitationAcceptance.Expired => "This invitation has expired. Ask an administrator of the workspace for a new one.",
        InvitationAcceptance.Revoked => "This invitation was withdrawn by an administrator. Ask them for a new one if you still need access.",
        InvitationAcceptance.AlreadyUsed => "This invitation has already been used. Each invitation creates one account — sign in instead, or ask for a new invitation.",
        InvitationAcceptance.EmailRequired => "Enter the email address the invitation was sent to.",
        InvitationAcceptance.EmailMismatch => "This invitation was sent to a different email address. Use the address it was sent to, or ask for an invitation to yours.",
        _ => "This invitation link isn't valid. Ask an administrator of the workspace for a new one.",
    };
}
