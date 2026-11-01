// Cross-cutting log hygiene. User-controlled values (role/tenant names, model ids, free text) must be
// neutralised before they reach a log sink: embedded newlines let an attacker forge extra log lines
// (CRLF / log-forging), and credential-shaped substrings (API keys, bearer tokens, connection-string
// passwords) must never leave the process readable — production logs get shipped to ELK/Splunk/App
// Insights where they outlive the secret's rotation.

using System;
using System.Text.RegularExpressions;

namespace AgentOs.SharedKernel.Logging;

/// <summary>Helpers that make a user-controlled value safe to write to a log sink.</summary>
public static partial class LogSafe
{
    /// <summary>Removes CR/LF (and tabs) so a tainted value cannot inject forged log lines
    /// (log-forging), then masks credential-shaped substrings. Returns <c>"(null)"</c> for null.</summary>
    public static string Scrub(string? value) =>
        value is null
            ? "(null)"
            : MaskSecrets(value
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Replace("\t", " ", StringComparison.Ordinal));

    /// <summary>Masks credential-shaped substrings: Anthropic keys (<c>sk-ant-…</c>, and generic
    /// <c>sk-…</c>), GitHub tokens (<c>ghp_/gho_/ghs_/ghu_/ghr_/github_pat_…</c>), <c>Bearer</c> and
    /// <c>Authorization: Basic</c> header values, bare JWTs (<c>eyJ….….…</c>), Azure DevOps personal access
    /// tokens (52-character base32 and the 84-character <c>…AZDO…</c> form), <c>Password=</c>/<c>Pwd=</c>
    /// connection-string segments and YAML/JSON-style <c>password:</c> / <c>token:</c> / <c>secret:</c> /
    /// <c>api_key:</c> values — all case-insensitive. The first 4 payload characters of a key are kept so an
    /// operator can still correlate WHICH key leaked.</summary>
    public static string MaskSecrets(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var masked = ApiKeyPattern().Replace(value, static m => $"{m.Groups[1].Value}{Head(m.Groups[2].Value)}***");
        masked = BearerPattern().Replace(masked, static m => $"{m.Groups[1].Value}{Head(m.Groups[2].Value)}***");
        masked = BasicAuthPattern().Replace(masked, static m => $"{m.Groups[1].Value}***");
        masked = JwtPattern().Replace(masked, "eyJ***");
        masked = AzureDevOpsPatPattern().Replace(masked, static m => $"{Head(m.Value)}***");
        masked = ConnectionPasswordPattern().Replace(masked, static m => $"{m.Groups[1].Value}***");
        masked = ColonSecretPattern().Replace(masked, static m => $"{m.Groups[1].Value}{m.Groups[2].Value}***");
        return masked;
    }

    private static string Head(string payload) => payload.Length <= 4 ? string.Empty : payload[..4];

    // sk-ant-api03-…, sk-…, ghp_…, gho_…, ghs_…, ghu_…, ghr_…, github_pat_… — prefix kept, payload masked.
    // IgnoreCase: an uppercased copy (env-var dumps, shouting logs) must not bypass the mask.
    [GeneratedRegex(@"\b(sk-ant-|sk-|ghp_|gho_|ghs_|ghu_|ghr_|github_pat_)([A-Za-z0-9_\-]{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyPattern();

    // "Authorization: Basic dXNlcjpwYXNz" (curl -H, HTTP dumps). Only after the header name, so the prose word
    // "Basic" is left alone.
    [GeneratedRegex(@"\b(Authorization\s*:\s*Basic\s+)([A-Za-z0-9+/]{8,}={0,2})", RegexOptions.IgnoreCase)]
    private static partial Regex BasicAuthPattern();

    // A bare JWT: base64url header (always starts eyJ — '{"'), payload and signature.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}")]
    private static partial Regex JwtPattern();

    // Azure DevOps PATs: the classic 52-character lowercase base32 token, and the 84-character form with "AZDO" at
    // position 77. Whole-word only, so they never match inside a longer identifier.
    [GeneratedRegex(@"\b(?:[a-z2-7]{52}|[A-Za-z0-9]{76}AZDO[A-Za-z0-9]{4})\b")]
    private static partial Regex AzureDevOpsPatPattern();

    // YAML / JSON / log "key: value" credentials — password: x, "token": "x", api_key: 'x'. The key word must end right
    // before the colon ("tokens: 500" is not one).
    [GeneratedRegex(@"\b(password|passwd|pwd|secret|client_secret|token|access_token|refresh_token|api[_\-]?key)(""?\s*:\s*[""']?)(?=[^\s""',;]{4,})[^\s""',;]+", RegexOptions.IgnoreCase)]
    private static partial Regex ColonSecretPattern();

    // Authorization header material: "Bearer eyJ…" / "Bearer abc.def.ghi".
    [GeneratedRegex(@"\b(Bearer\s+)([A-Za-z0-9._\-]{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    // Connection-string credentials: Password=… / Pwd=… up to the next ';'.
    [GeneratedRegex(@"\b(Password\s*=\s*|Pwd\s*=\s*)([^;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionPasswordPattern();
}
