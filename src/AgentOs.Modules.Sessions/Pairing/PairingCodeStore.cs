// Short-lived, single-use exchange codes for the VS Code browser-pairing flow. The browser redirect to
// the editor carries only the CODE (never the token); the extension exchanges it once, over a direct
// POST, for the actual runner credentials. In-memory + bounded by TTL — a pairing is completed within
// seconds, so there is nothing durable to persist.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;

namespace AgentOs.Modules.Sessions.Pairing;

/// <summary>What a one-time pairing code redeems to — everything the runner needs to connect.</summary>
internal sealed record PairingPayload(Guid RunnerId, string Token, string HubUrl);

/// <summary>A code a person types on the computer they connect (Board › Computers › Connect this computer).</summary>
public interface IComputerPairingCodes
{
    /// <summary>A one-time code like <c>K7F2-9QXM</c>, valid for <see cref="PairingCodeStore.TypedTtl"/>, that the runner's
    /// <c>pair</c> command exchanges for these credentials.</summary>
    string IssueTypedCode(Guid runnerId, string token, string hubUrl);
}

/// <summary>Issues + redeems one-time, short-lived pairing codes.</summary>
internal interface IPairingCodeStore
{
    /// <summary>Stash a payload and return a one-time code valid for <see cref="PairingCodeStore.Ttl"/>.</summary>
    string Stash(PairingPayload payload);

    /// <summary>Redeem and remove a code. Null when the code is unknown, already used, or expired.</summary>
    PairingPayload? Redeem(string code);
}

/// <inheritdoc />
internal sealed class PairingCodeStore : IPairingCodeStore, IComputerPairingCodes
{
    /// <summary>How long a code stays valid — the extension exchanges it immediately after the redirect.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>A typed code: long enough to walk to the other computer and type it, short enough to be useless later.</summary>
    public static readonly TimeSpan TypedTtl = TimeSpan.FromMinutes(10);

    // No 0/O, 1/I/L, U — nothing a person misreads. 8 of 30 symbols ≈ 39 bits; single use, 10 minutes, and the
    // exchange endpoint is rate-limited per client (Web Program.cs), so guessing a live code is out of reach.
    private const string TypedAlphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (PairingPayload Payload, DateTimeOffset ExpiresAt)> _codes =
        new(StringComparer.Ordinal);

    public PairingCodeStore(TimeProvider clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <inheritdoc />
    public string Stash(PairingPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Prune();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _codes[code] = (payload, _clock.GetUtcNow().Add(Ttl));
        return code;
    }

    /// <inheritdoc />
    public string IssueTypedCode(Guid runnerId, string token, string hubUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(hubUrl);
        Prune();
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = TypedAlphabet[RandomNumberGenerator.GetInt32(TypedAlphabet.Length)];
        }
        var code = new string(chars);
        _codes[code] = (new PairingPayload(runnerId, token, hubUrl), _clock.GetUtcNow().Add(TypedTtl));
        return string.Concat(code.AsSpan(0, 4), "-", code.AsSpan(4));
    }

    /// <summary>A typed code as stored: upper case, no dash or spaces ("k7f2 9qxm" → "K7F29QXM").</summary>
    internal static string Normalize(string code) =>
        string.Concat(code.Where(c => c is not ('-' or ' '))).ToUpperInvariant();

    /// <inheritdoc />
    public PairingPayload? Redeem(string code)
    {
        if (string.IsNullOrEmpty(code)) { return null; }
        if (!_codes.TryRemove(code, out var entry) && !_codes.TryRemove(Normalize(code), out entry))
        {
            return null;
        }
        return entry.ExpiresAt > _clock.GetUtcNow() ? entry.Payload : null;
    }

    // Drop expired codes opportunistically on each Stash so the dictionary can't grow unbounded from
    // abandoned pairings.
    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var kvp in _codes.Where(kvp => kvp.Value.ExpiresAt <= now))
        {
            _codes.TryRemove(kvp.Key, out _);
        }
    }
}
