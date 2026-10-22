// The runner keeps the computer from idle-sleeping only while work is in flight, and lets go when it ends.

using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public class KeepAwakeTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData(" OFF ", false)]
    public void Allowed_OnlyOffOptsOut(string? setting, bool expected) => KeepAwake.Allowed(setting).ShouldBe(expected);

    [Fact]
    public void Hold_CountsWorkInFlight_AndReleasesOnce()
    {
        using var awake = new KeepAwake(enabled: true);
        var first = awake.Hold();
        var second = awake.Hold();
        awake.Active.ShouldBe(2);

        first.Dispose();
        first.Dispose(); // a double release must not under-count
        awake.Active.ShouldBe(1);

        second.Dispose();
        awake.Active.ShouldBe(0);
    }
}
