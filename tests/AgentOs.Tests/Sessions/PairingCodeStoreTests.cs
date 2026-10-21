// Unit tests for PairingCodeStore — the one-time, short-lived exchange codes that carry a runner's
// credentials from the browser approve step to the editor extension (the token never rides a URL).

using System;
using AgentOs.Modules.Sessions.Pairing;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public class PairingCodeStoreTests
{
    private static PairingPayload Payload() =>
        new(Guid.NewGuid(), "tok-abc", "https://localhost:5180/hubs/remote-agent");

    [Fact]
    public void Stash_ThenRedeem_ReturnsPayload_AndIsSingleUse()
    {
        var store = new PairingCodeStore(TimeProvider.System);
        var payload = Payload();

        var code = store.Stash(payload);
        var first = store.Redeem(code);

        first.ShouldNotBeNull();
        first!.RunnerId.ShouldBe(payload.RunnerId);
        first.Token.ShouldBe("tok-abc");
        first.HubUrl.ShouldBe(payload.HubUrl);

        store.Redeem(code).ShouldBeNull(); // already consumed
    }

    [Fact]
    public void Redeem_UnknownCode_IsNull()
    {
        new PairingCodeStore(TimeProvider.System).Redeem("not-a-real-code").ShouldBeNull();
    }

    [Fact]
    public void Redeem_AfterTtl_IsNull()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var store = new PairingCodeStore(clock);

        var code = store.Stash(Payload());
        clock.Advance(PairingCodeStore.Ttl + TimeSpan.FromSeconds(1));

        store.Redeem(code).ShouldBeNull();
    }

    // Board › Computers: a code a person reads off one screen and types on another computer.
    [Fact]
    public void IssueTypedCode_IsShortReadableAndRedeemsOnceWhateverTheCaseOrDash()
    {
        var store = new PairingCodeStore(TimeProvider.System);
        var runner = Guid.NewGuid();

        var code = store.IssueTypedCode(runner, "tok-typed", "https://lab.dev.localhost:6180/hubs/remote-agent");

        code.ShouldMatch("^[2-9A-HJKMNP-TV-Z]{4}-[2-9A-HJKMNP-TV-Z]{4}$");
        var typed = code.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant();
        var payload = store.Redeem(typed);
        payload.ShouldNotBeNull();
        payload!.RunnerId.ShouldBe(runner);
        payload.Token.ShouldBe("tok-typed");
        store.Redeem(code).ShouldBeNull();
    }

    [Fact]
    public void IssueTypedCode_LastsTenMinutesNotSixtySeconds()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var store = new PairingCodeStore(clock);
        var kept = store.IssueTypedCode(Guid.NewGuid(), "a", "https://h/hubs/remote-agent");
        var expired = store.IssueTypedCode(Guid.NewGuid(), "b", "https://h/hubs/remote-agent");

        clock.Advance(TimeSpan.FromMinutes(5));
        store.Redeem(kept).ShouldNotBeNull();

        clock.Advance(PairingCodeStore.TypedTtl);
        store.Redeem(expired).ShouldBeNull();
    }

    private sealed class FixedClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
