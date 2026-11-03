// GAP-11b — the audit hash chain and its verifier, without a database: a chain built the way EfAuditLog builds it
// verifies intact; changing any field of an event, deleting one, swapping two, or re-hashing a changed event is reported
// at the first event that no longer holds; the encoding is unambiguous (moving text between fields changes the hash);
// timestamps are hashed at the microsecond Postgres stores.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Persistence.Entities;
using AgentOs.Modules.Tenants.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class AuditChainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 8, 30, 15, TimeSpan.Zero);

    [Fact]
    public void Verify_ChainAsWritten_IsIntact_WithTheHeadHash()
    {
        var chain = Chain(5);

        var report = AuditChain.Verify("acme", chain, unchained: 2);

        report.Intact.ShouldBeTrue();
        report.Verified.ShouldBe(5);
        report.Unchained.ShouldBe(2);
        report.HeadHash.ShouldBe(chain[^1].Hash);
        report.BrokenAt.ShouldBeNull();
        report.Summary.ShouldBe("Chain intact — 5 events verified; 2 earlier events predate the chain.");
    }

    [Fact]
    public void Verify_EmptyChain_SaysThereIsNothingToVerify()
    {
        var report = AuditChain.Verify("acme", [], unchained: 0);
        report.Intact.ShouldBeTrue();
        report.Verified.ShouldBe(0);
        report.HeadHash.ShouldBeNull();
        report.Summary.ShouldStartWith("Nothing to verify yet");
    }

    public static TheoryData<string> Fields => ["user", "action", "target", "ip", "agent", "time", "tenant", "id"];

    [Theory]
    [MemberData(nameof(Fields))]
    public void Verify_AnyFieldChanged_IsAlteredAtThatEvent(string field)
    {
        var chain = Chain(5);
        var e = chain[2];
        chain[2] = field switch
        {
            "user" => e with { UserId = "someone-else" },
            "action" => e with { Action = "member.invited" },
            "target" => e with { Target = e.Target + "!" },
            "ip" => e with { IpAddress = "10.9.9.9" },
            "agent" => e with { UserAgent = null },
            "time" => e with { TimestampUtc = e.TimestampUtc.AddSeconds(1) },
            "tenant" => e with { TenantId = "other" },
            _ => e with { Id = Guid.NewGuid() },
        };

        var report = AuditChain.Verify("acme", chain, 0);

        report.Intact.ShouldBeFalse();
        report.Problem.ShouldBe(AuditChainProblem.Altered);
        report.BrokenAt.ShouldBe(3);
        report.BrokenEventId.ShouldBe(chain[2].Id);
        report.Verified.ShouldBe(2);
        report.HeadHash.ShouldBeNull();
        report.Summary.ShouldBe("Chain broken at event 3: its content no longer matches its hash — it was changed after it was written.");
    }

    // Rewriting an event AND its own hash is caught by the next event's link.
    [Fact]
    public void Verify_ChangedEventRehashed_IsRelinkedAtTheNextEvent()
    {
        var chain = Chain(5);
        var changed = chain[2] with { Target = "rewritten" };
        chain[2] = changed with { Hash = AuditChain.Compute(changed) };

        var report = AuditChain.Verify("acme", chain, 0);

        report.Problem.ShouldBe(AuditChainProblem.Relinked);
        report.BrokenAt.ShouldBe(4);
        report.Summary.ShouldBe("Chain broken at event 4: it doesn't follow event 3 — an earlier event was changed or replaced.");
    }

    [Fact]
    public void Verify_EventDeletedInTheMiddle_IsMissing()
    {
        var chain = Chain(5);
        chain.RemoveAt(1);   // event 2

        var report = AuditChain.Verify("acme", chain, 0);

        report.Problem.ShouldBe(AuditChainProblem.Missing);
        report.BrokenAt.ShouldBe(2);
        report.BrokenEventId.ShouldBeNull();
        report.Verified.ShouldBe(1);
        report.Summary.ShouldBe("Chain broken at event 2: it is missing — an event was deleted.");
    }

    [Fact]
    public void Verify_FirstEventDeleted_IsMissingAtOne()
        => AuditChain.Verify("acme", Chain(3).Skip(1), 0).BrokenAt.ShouldBe(1);

    [Fact]
    public void Verify_TwoEventsSwapped_IsReported()
    {
        var chain = Chain(4);
        (chain[1], chain[2]) = (chain[2], chain[1]);

        var report = AuditChain.Verify("acme", chain, 0);

        report.Intact.ShouldBeFalse();
        report.BrokenAt.ShouldBe(2);
    }

    // Known limit (threat model MT-3): deleting the newest events is not visible to the chain alone — the head hash the
    // verifier shows is what an operator records elsewhere to catch it.
    [Fact]
    public void Verify_TailTruncated_StillIntact_ButTheHeadMoves()
    {
        var chain = Chain(5);
        var full = AuditChain.Verify("acme", chain, 0);
        var truncated = AuditChain.Verify("acme", chain.Take(3), 0);

        truncated.Intact.ShouldBeTrue();
        truncated.HeadHash.ShouldNotBe(full.HeadHash);
    }

    [Fact]
    public async Task VerifyAsync_MatchesVerify()
    {
        var chain = Chain(6);
        chain[4] = chain[4] with { Action = "x" };

        var streamed = await AuditChain.VerifyAsync("acme", Stream(chain), 1);

        streamed.ShouldBe(AuditChain.Verify("acme", chain, 1));
    }

    // Length-prefixed encoding: the same characters split differently between fields hash differently.
    [Fact]
    public void Compute_MovingTextBetweenFields_ChangesTheHash()
    {
        var a = Link(1, AuditChain.Genesis) with { UserId = "ab", Target = "c" };
        var b = a with { UserId = "a", Target = "bc" };
        var c = a with { UserId = null, Target = "abc" };
        var d = a with { UserId = "", Target = "abc" };

        new[] { a, b, c, d }.Select(AuditChain.Compute).Distinct().Count().ShouldBe(4);
    }

    [Fact]
    public void Compute_IsStable_AndHex()
    {
        var link = Link(1, AuditChain.Genesis);
        var hash = AuditChain.Compute(link);
        hash.ShouldBe(AuditChain.Compute(link));
        hash.Length.ShouldBe(64);
        hash.ShouldAllBe(c => "0123456789abcdef".Contains(c));
    }

    // Postgres keeps microseconds: a sub-microsecond difference is the same stored value, so the same hash.
    [Fact]
    public void Compute_HashesTheTimestampAtMicrosecondPrecision_InUtc()
    {
        var link = Link(1, AuditChain.Genesis);
        AuditChain.Compute(link with { TimestampUtc = link.TimestampUtc.AddTicks(7) }).ShouldBe(AuditChain.Compute(link));
        AuditChain.Compute(link with { TimestampUtc = link.TimestampUtc.ToOffset(TimeSpan.FromHours(7)) }).ShouldBe(AuditChain.Compute(link));
        AuditChain.Compute(link with { TimestampUtc = link.TimestampUtc.AddTicks(10) }).ShouldNotBe(AuditChain.Compute(link));
        AuditChain.Normalize(new DateTimeOffset(638_000_000_000_000_017, TimeSpan.Zero)).UtcTicks.ShouldBe(638_000_000_000_000_010);
    }

    [Fact]
    public void Clip_KeepsColumnLimits_WithoutSplittingASurrogatePair()
    {
        EfAuditLog.Clip(null, 5).ShouldBeNull();
        EfAuditLog.Clip("short", 5).ShouldBe("short");
        EfAuditLog.Clip("toolong", 5).ShouldBe("toolo");
        EfAuditLog.Clip("abcd\U0001F600", 5).ShouldBe("abcd");   // the emoji's high surrogate would sit at index 4
    }

    // The writer's row → link mapping is what the verifier reads back: a freshly built row verifies.
    [Fact]
    public void LinkOf_RowBuiltByTheWriter_Verifies()
    {
        var row = new AuditEventEntity
        {
            Id = Guid.NewGuid(), TenantId = "acme", UserId = "u", Action = AuditActions.InvitationMinted, Target = "t",
            TimestampUtc = AuditChain.Normalize(T0), Sequence = 1, PrevHash = AuditChain.Genesis,
        };
        row.Hash = AuditChain.Compute(EfAuditLog.LinkOf(row));

        AuditChain.Verify("acme", [EfAuditLog.LinkOf(row)], 0).Intact.ShouldBeTrue();
    }

    // ── helpers ──

    private static List<AuditChainLink> Chain(int count)
    {
        var links = new List<AuditChainLink>(count);
        var previous = AuditChain.Genesis;
        for (var i = 1; i <= count; i++)
        {
            var link = Link(i, previous);
            link = link with { Hash = AuditChain.Compute(link) };
            links.Add(link);
            previous = link.Hash;
        }
        return links;
    }

    private static AuditChainLink Link(long sequence, string previous) => new(
        sequence, Guid.NewGuid(), "acme", T0.AddMinutes(sequence), "user-" + sequence, AuditActions.SettingChanged,
        $"Agents:Coding:Model: a → b{sequence}", "203.0.113.7", "Mozilla/5.0", previous, string.Empty);

    private static async IAsyncEnumerable<AuditChainLink> Stream(IEnumerable<AuditChainLink> links)
    {
        foreach (var link in links)
        {
            await Task.Yield();
            yield return link;
        }
    }
}
