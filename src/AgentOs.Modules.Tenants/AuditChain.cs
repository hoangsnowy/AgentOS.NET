// GAP-11b — the audit trail's hash chain. Each tenant's events (and the "_platform" scope's) form one chain: event N
// stores the hash of event N-1 (Genesis for the first) and its own SHA-256 over a canonical, length-prefixed encoding of
// its content plus that previous hash. Changing an event breaks its own hash; removing or re-ordering one breaks the
// sequence or the next link. The verifier walks a chain in order (streaming, O(1) memory) and names the first event
// that doesn't hold.
// Unchained rows (no Sequence / Hash) are legitimate only from before the chain existed. Once a tenant's chain has
// started, an unchained row recorded at or after its first event is an insert that bypassed the writer (the app role may
// INSERT): the verifier reports the chain broken at the position that row takes in time (AuditChainProblem.Inserted).
// Limits, by design (docs/security/threat-model.md MT-3): a database owner who rewrites an event AND every later hash
// is not detected without an external anchor — the head hash is shown so it can be recorded elsewhere; deleting the
// newest events (truncating the tail) is not detected by the chain alone either. The app role cannot do either: it may
// only INSERT and SELECT audit rows (REVOKE UPDATE, DELETE, TRUNCATE — RowLevelSecurity.AppendOnlyTables). An unchained
// row back-dated to before the chain's first event reads as pre-chain history — it is counted, never verified.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Modules.Tenants;

/// <summary>One chained audit event, as stored (the verifier's input).</summary>
public sealed record AuditChainLink(
    long Sequence,
    Guid Id,
    string TenantId,
    DateTimeOffset TimestampUtc,
    string? UserId,
    string Action,
    string? Target,
    string? IpAddress,
    string? UserAgent,
    string PrevHash,
    string Hash);

/// <summary>Why a chain doesn't hold.</summary>
public enum AuditChainProblem
{
    /// <summary>Every event checked out.</summary>
    None = 0,

    /// <summary>An event is missing: the sequence jumps (an event was deleted).</summary>
    Missing = 1,

    /// <summary>An event's content no longer matches its own hash (it was changed after it was written).</summary>
    Altered = 2,

    /// <summary>An event doesn't link to the one before it (an earlier event was changed or replaced).</summary>
    Relinked = 3,

    /// <summary>An event without a chain link was recorded after the chain started (it was added outside the audit
    /// writer).</summary>
    Inserted = 4,
}

/// <summary>The earliest unchained event recorded at or after a chain's first event — an insert that bypassed the
/// writer (the verifier's input; null when there is none).</summary>
/// <param name="Id">The event's id.</param>
/// <param name="TimestampUtc">When it claims to have happened.</param>
public sealed record AuditChainStray(Guid Id, DateTimeOffset TimestampUtc);

/// <summary>The verifier's answer for one tenant's chain.</summary>
/// <param name="TenantId">The chain (a tenant id, or <see cref="AuditScopes.Platform"/>).</param>
/// <param name="Verified">Chained events checked before the first problem (all of them when intact).</param>
/// <param name="Unchained">Events recorded before the chain existed — kept, but not verifiable.</param>
/// <param name="HeadHash">The newest event's hash when intact (record it elsewhere to anchor the chain), else null.</param>
/// <param name="BrokenAt">The sequence number of the first event that doesn't hold, else null. For an
/// <see cref="AuditChainProblem.Inserted"/> event: the position it takes in time (after event <c>BrokenAt - 1</c>).</param>
/// <param name="BrokenEventId">That event's id, when it exists (null for a missing one).</param>
/// <param name="Problem">What is wrong at <paramref name="BrokenAt"/>.</param>
public sealed record AuditChainReport(
    string TenantId,
    long Verified,
    long Unchained,
    string? HeadHash,
    long? BrokenAt,
    Guid? BrokenEventId,
    AuditChainProblem Problem)
{
    /// <summary>True when no event failed.</summary>
    public bool Intact => Problem == AuditChainProblem.None;

    /// <summary>One plain-language line for the desktop and the Api.</summary>
    public string Summary => Problem switch
    {
        AuditChainProblem.None when Verified == 0 => Unchained == 0
            ? "Nothing to verify yet — no events recorded."
            : $"Nothing to verify yet — the {Unchained} recorded events predate the chain.",
        AuditChainProblem.None => $"Chain intact — {Verified} events verified{(Unchained > 0 ? $"; {Unchained} earlier events predate the chain" : string.Empty)}.",
        AuditChainProblem.Missing => $"Chain broken at event {BrokenAt}: it is missing — an event was deleted.",
        AuditChainProblem.Altered => $"Chain broken at event {BrokenAt}: its content no longer matches its hash — it was changed after it was written.",
        AuditChainProblem.Inserted => $"Chain broken at event {BrokenAt}: an event without a chain link was recorded there — it was added outside the audit log.",
        _ => $"Chain broken at event {BrokenAt}: it doesn't follow event {BrokenAt - 1} — an earlier event was changed or replaced.",
    };
}

/// <summary>Computes and verifies the audit hash chain (see the file header).</summary>
public static class AuditChain
{
    /// <summary>The previous hash of a chain's first event.</summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    // Bumped only with a new encoding; the verifier would then pick the encoding by version.
    private const string Version = "agentos-audit-chain-v1";

    /// <summary>Postgres keeps a timestamp to the microsecond: hash exactly what is stored, in UTC.</summary>
    public static DateTimeOffset Normalize(DateTimeOffset timestamp)
    {
        var ticks = timestamp.UtcTicks;
        return new DateTimeOffset(ticks - (ticks % 10), TimeSpan.Zero);
    }

    /// <summary>The hash of <paramref name="link"/> (its own <see cref="AuditChainLink.Hash"/> is not an input).</summary>
    public static string Compute(AuditChainLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Field(hash, Version);
        Field(hash, link.PrevHash);
        Field(hash, link.TenantId);
        Field(hash, link.Sequence.ToString(CultureInfo.InvariantCulture));
        Field(hash, link.Id.ToString("D"));
        Field(hash, Normalize(link.TimestampUtc).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
        Field(hash, link.UserId);
        Field(hash, link.Action);
        Field(hash, link.Target);
        Field(hash, link.IpAddress);
        Field(hash, link.UserAgent);
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Walk a chain in sequence order and report the first event that doesn't hold. <paramref name="stray"/> is
    /// the earliest unchained event recorded at or after the chain's first event (see the file header), if any.</summary>
    public static AuditChainReport Verify(string tenantId, IEnumerable<AuditChainLink> links, long unchained, AuditChainStray? stray = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        var walk = new Walk(tenantId, unchained, stray);
        foreach (var link in links)
        {
            if (walk.Step(link) is { } broken) { return broken; }
        }
        return walk.Done();
    }

    /// <summary>Streaming twin of <see cref="Verify"/> (a database cursor; O(1) memory).</summary>
    public static async Task<AuditChainReport> VerifyAsync(
        string tenantId, IAsyncEnumerable<AuditChainLink> links, long unchained, AuditChainStray? stray = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(links);
        var walk = new Walk(tenantId, unchained, stray);
        await foreach (var link in links.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (walk.Step(link) is { } broken) { return broken; }
        }
        return walk.Done();
    }

    // "<utf-8 byte count>:<utf-8 bytes>\n", or "-\n" for null: unambiguous whatever a value contains.
    private static void Field(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            hash.AppendData("-\n"u8);
            return;
        }
        var count = Encoding.UTF8.GetByteCount(value);
        Span<byte> prefix = stackalloc byte[16];
        _ = count.TryFormat(prefix, out var written, default, CultureInfo.InvariantCulture);   // ≤ 10 digits: always fits
        prefix[written++] = (byte)':';
        hash.AppendData(prefix[..written]);
        byte[]? rented = null;
        Span<byte> buffer = count <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(count));
        try
        {
            var bytes = Encoding.UTF8.GetBytes(value, buffer);
            hash.AppendData(buffer[..bytes]);
        }
        finally
        {
            if (rented is not null) { ArrayPool<byte>.Shared.Return(rented); }
        }
        hash.AppendData("\n"u8);
    }

    private sealed class Walk(string tenantId, long unchained, AuditChainStray? stray)
    {
        private long _expected = 1;
        private string _previous = Genesis;
        private AuditChainStray? _stray = stray;

        public AuditChainReport? Step(AuditChainLink link)
        {
            if (_stray is { } s && s.TimestampUtc < link.TimestampUtc)
            {
                // Before the first event: pre-chain history. Later: it sits in front of this event — reported here.
                if (_expected > 1) { return Broken(_expected, s.Id, AuditChainProblem.Inserted); }
                _stray = null;
            }
            if (link.Sequence != _expected)
            {
                // A jump: event _expected is gone. (A repeat can't happen — the (tenant, sequence) index is unique.)
                return Broken(_expected, link.Sequence > _expected ? null : link.Id, AuditChainProblem.Missing);
            }
            if (!string.Equals(Compute(link), link.Hash, StringComparison.Ordinal))
            {
                return Broken(link.Sequence, link.Id, AuditChainProblem.Altered);
            }
            if (!string.Equals(link.PrevHash, _previous, StringComparison.Ordinal))
            {
                return Broken(link.Sequence, link.Id, AuditChainProblem.Relinked);
            }
            _previous = link.Hash;
            _expected++;
            return null;
        }

        public AuditChainReport Done()
            => _stray is { } s && _expected > 1
                ? Broken(_expected, s.Id, AuditChainProblem.Inserted)
                : new(tenantId, _expected - 1, unchained, _expected == 1 ? null : _previous, null, null, AuditChainProblem.None);

        private AuditChainReport Broken(long at, Guid? id, AuditChainProblem problem)
            => new(tenantId, _expected - 1, unchained, null, at, id, problem);
    }
}
