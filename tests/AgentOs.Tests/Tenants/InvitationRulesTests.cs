// GAP-11 — the invitation rules as pure functions: state precedence (revoked > used > expired > pending), acceptance only
// for the invited address (case-insensitive, surrounding whitespace ignored), and a distinct plain-language reason for
// every refusal.

using System;
using System.Linq;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Invitations;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class InvitationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("bob@acme.test", "bob@acme.test")]
    [InlineData("  Bob@Acme.TEST ", "bob@acme.test")]
    [InlineData("ÉLISE@Exemple.fr", "élise@exemple.fr")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizeEmail_TrimsAndLowerCases(string? input, string expected)
        => InvitationRules.NormalizeEmail(input).ShouldBe(expected);

    [Fact]
    public void StatusOf_Pending_BeforeExpiry()
        => InvitationRules.StatusOf(Now.AddMinutes(1), null, null, Now).ShouldBe(InvitationStatus.Pending);

    [Fact]
    public void StatusOf_Expired_AtAndAfterExpiry()
    {
        InvitationRules.StatusOf(Now, null, null, Now).ShouldBe(InvitationStatus.Expired);
        InvitationRules.StatusOf(Now.AddHours(-1), null, null, Now).ShouldBe(InvitationStatus.Expired);
    }

    // A used invitation says "used" even after it expires; a revoked one says "revoked" whatever else happened.
    [Fact]
    public void StatusOf_Precedence_RevokedThenUsedThenExpired()
    {
        InvitationRules.StatusOf(Now.AddHours(-1), acceptedAtUtc: Now.AddHours(-2), null, Now).ShouldBe(InvitationStatus.Accepted);
        InvitationRules.StatusOf(Now.AddHours(1), Now.AddMinutes(-5), revokedAtUtc: Now.AddMinutes(-1), Now).ShouldBe(InvitationStatus.Revoked);
        InvitationRules.StatusOf(Now.AddHours(-1), null, Now.AddHours(-2), Now).ShouldBe(InvitationStatus.Revoked);
    }

    [Theory]
    [InlineData("bob@acme.test")]
    [InlineData("BOB@ACME.TEST")]
    [InlineData(" bob@acme.test\t")]
    public void CheckAcceptance_PendingForTheInvitedAddress_IsOk(string presented)
        => InvitationRules.CheckAcceptance(InvitationStatus.Pending, "bob@acme.test", presented).ShouldBe(InvitationAcceptance.Ok);

    [Theory]
    [InlineData("eve@acme.test")]
    [InlineData("bob@acme.test.evil")]
    [InlineData("bob+x@acme.test")]
    [InlineData("bob@acme.tes")]
    public void CheckAcceptance_AnotherAddress_IsEmailMismatch(string presented)
        => InvitationRules.CheckAcceptance(InvitationStatus.Pending, "bob@acme.test", presented).ShouldBe(InvitationAcceptance.EmailMismatch);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void CheckAcceptance_NoAddress_IsEmailRequired(string? presented)
        => InvitationRules.CheckAcceptance(InvitationStatus.Pending, "bob@acme.test", presented).ShouldBe(InvitationAcceptance.EmailRequired);

    // Reuse / revoked / expired are refused whoever asks — even the invited address.
    [Theory]
    [InlineData(InvitationStatus.Accepted, InvitationAcceptance.AlreadyUsed)]
    [InlineData(InvitationStatus.Revoked, InvitationAcceptance.Revoked)]
    [InlineData(InvitationStatus.Expired, InvitationAcceptance.Expired)]
    public void CheckAcceptance_NotPending_IsRefusedEvenForTheInvitedAddress(InvitationStatus status, InvitationAcceptance expected)
    {
        InvitationRules.CheckAcceptance(status, "bob@acme.test", "bob@acme.test").ShouldBe(expected);
        InvitationRules.ForStatus(status).ShouldBe(expected);
    }

    [Fact]
    public void Describe_EveryRefusal_HasItsOwnPlainLanguageReason()
    {
        var refusals = Enum.GetValues<InvitationAcceptance>().Where(o => o != InvitationAcceptance.Ok).ToList();
        var messages = refusals.Select(InvitationRules.Describe).ToList();
        messages.ShouldAllBe(m => m.Length > 20);
        messages.Distinct(StringComparer.Ordinal).Count().ShouldBe(refusals.Count);
    }
}
