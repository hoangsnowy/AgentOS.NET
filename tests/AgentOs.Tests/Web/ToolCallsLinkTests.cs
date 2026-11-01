// A Board task's activity panel (and Insights) deep-links to Settings › Activity log › Tool calls, filtered to the calls
// made on a member's computer — optionally one task's. The hub parses the link; anything unexpected is dropped.

using System;
using AgentOs.Domain.Tools;
using AgentOs.Web.Services;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class ToolCallsLinkTests
{
    [Fact]
    public void MemberComputerToolCalls_ForATask_RoundTrips()
    {
        var session = Guid.NewGuid();

        var (key, origin, sessionId) = SettingsLinks.ParseToolCalls(SettingsLinks.Normalize(SettingsLinks.MemberComputerToolCalls(session)));

        key.ShouldBe(SettingsLinks.ToolCalls);
        origin.ShouldBe(ToolEvidenceOrigins.MemberComputer);
        sessionId.ShouldBe(session.ToString("D"));
    }

    [Fact]
    public void MemberComputerToolCalls_WithoutATask_HasNoSession() =>
        SettingsLinks.ParseToolCalls(SettingsLinks.MemberComputerToolCalls()).ShouldBe((SettingsLinks.ToolCalls, ToolEvidenceOrigins.MemberComputer, null));

    [Theory]
    [InlineData("evidence?origin=server&session=not-a-guid", "evidence")]
    [InlineData("evidence?origin=<script>", "evidence")]
    [InlineData("keys?origin=member-computer", "keys")]
    [InlineData("audit", "audit")]
    public void ParseToolCalls_UnexpectedValues_AreDropped(string target, string expectedKey) =>
        SettingsLinks.ParseToolCalls(target).ShouldBe((expectedKey, null, null));
}
