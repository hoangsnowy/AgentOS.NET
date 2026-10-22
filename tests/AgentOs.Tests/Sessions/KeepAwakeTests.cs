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
        using (var first = awake.Hold())
        {
            using (awake.Hold())
            {
                awake.Active.ShouldBe(2);
                first.Dispose();
                first.Dispose(); // a double release must not under-count
                awake.Active.ShouldBe(1);
            }
            awake.Active.ShouldBe(0);
        }
        awake.Active.ShouldBe(0); // the outer using's second release is a no-op too
    }
}
