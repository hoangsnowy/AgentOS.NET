// AgentOs.Domain/Tools/SecretRedactor.cs
// Governance of tool calls made on a member's computer — the one rule for what may leave it as evidence. The runner
// summarises every tool call its CLI made (the Bash command, the edited file's path) and the server records it; both
// sides apply THIS function (the runner links this file, BCL-only), so a summary the runner redacted is re-checked with
// exactly the same rules before it is stored. A summary is the FIRST line only (a heredoc's body is file content),
// credential-shaped text is replaced by "[redacted]", and the result is bounded (500 characters by default).
//
// Redacted: Authorization / API-key / cookie headers, Bearer/Basic values, well-known token shapes (Anthropic/OpenAI
// sk-…, GitHub ghp_/github_pat_, GitLab, Slack, AWS access keys, Google API keys, npm, JWTs), credentials in URLs
// (https://user:pass@host), key=value secrets (Password=…;, GITHUB_TOKEN=…, --token=…), secret flags with a separate
// value (--password x, --token x), curl's -u user:pass, a short -p/-P/-a password flag on credential-taking clients
// (mysql -psecret, sshpass -p x, sqlcmd -P x, redis-cli -a x, docker login -p x) and private-key blocks. A pattern
// that times out redacts the whole text (fail closed).

using System;
using System.Text.RegularExpressions;

namespace AgentOs.Domain.Tools;

/// <summary>Redacts credential-shaped text and bounds a one-line summary of a tool call's input.</summary>
public static partial class SecretRedactor
{
    /// <summary>What a secret is replaced by.</summary>
    public const string Mask = "[redacted]";

    /// <summary>The longest summary (characters), the ellipsis included.</summary>
    public const int DefaultMaxLength = 500;

    private const int TimeoutMs = 250;

    /// <summary>Replace every credential-shaped part of <paramref name="text"/> by <see cref="Mask"/>.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return string.Empty; }
        try
        {
            var s = PrivateKey().Replace(text, "[redacted private key]");
            s = UrlCredentials().Replace(s, m => m.Groups["scheme"].Value + m.Groups["user"].Value + ":" + Mask + "@");
            s = SecretHeader().Replace(s, m => m.Groups["name"].Value + ": " + Mask);
            s = AuthScheme().Replace(s, m => m.Groups["scheme"].Value + " " + Mask);
            s = SecretFlagWithValue().Replace(s, m => m.Groups["flag"].Value + " " + Mask);
            s = UserPassFlag().Replace(s, m => m.Groups["flag"].Value + " " + m.Groups["user"].Value + ":" + Mask);
            if (CredentialClient().IsMatch(s))
            {
                s = ShortPasswordFlag().Replace(s, m => m.Groups["flag"].Value + " " + Mask);
            }
            s = KeyValueSecret().Replace(s, m => m.Groups["key"].Value + m.Groups["quote"].Value + m.Groups["sep"].Value + Mask);
            s = KnownToken().Replace(s, Mask);
            return s;
        }
        catch (RegexMatchTimeoutException)
        {
            return Mask; // fail closed: text the rules could not finish checking never leaves as it was
        }
    }

    /// <summary>A bounded, redacted one-line summary of <paramref name="text"/>: its first non-empty line (a note
    /// says how many lines followed — never their content), secrets redacted, at most <paramref name="maxLength"/>
    /// characters.</summary>
    public static string Summary(string? text, int maxLength = DefaultMaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 16);
        if (string.IsNullOrWhiteSpace(text)) { return string.Empty; }
        var lines = text.Trim().Split('\n');
        var first = lines[0].TrimEnd('\r').Trim();
        var more = lines.Length - 1;
        var summary = Redact(first);
        if (more > 0)
        {
            summary += more == 1 ? " (+1 more line)" : $" (+{more.ToString(System.Globalization.CultureInfo.InvariantCulture)} more lines)";
        }
        return Bound(summary, maxLength);
    }

    /// <summary>Cut <paramref name="text"/> to <paramref name="maxLength"/> characters (an ellipsis marks the cut; a
    /// surrogate pair is never split). Control characters become spaces, so a summary is always one line.</summary>
    public static string Bound(string? text, int maxLength = DefaultMaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        if (string.IsNullOrEmpty(text)) { return string.Empty; }
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i])) { chars[i] = ' '; }
        }
        var s = new string(chars);
        if (s.Length <= maxLength) { return s; }
        var cut = maxLength - 1;
        if (char.IsHighSurrogate(s[cut - 1])) { cut--; }
        return string.Concat(s.AsSpan(0, cut), "…");
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PrivateKey();

    // scheme://user:password@host — the user stays, the password goes.
    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]{1,15}://)(?<user>[^\s:/@""']+):(?<pass>[^\s@/""']+)@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UrlCredentials();

    // A header name only where a header starts (line start, after a space, a quote or a brace) — never "x-access-token:" as
    // the user part of a URL, which UrlCredentials already handled (and whose host must stay readable).
    [GeneratedRegex(@"(?:^|(?<=[\s""'{,]))(?<name>proxy-authorization|authorization|x-api-key|api-key|x-auth-token|x-access-token|private-token|set-cookie|cookie)\s*:\s*(?<value>[^""'\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SecretHeader();

    [GeneratedRegex(@"\b(?<scheme>bearer|basic)\s+(?<value>[A-Za-z0-9._~+/=-]{8,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex AuthScheme();

    // --password value, --token "value" (the "=" forms are KeyValueSecret's).
    [GeneratedRegex(@"(?<flag>--(?:password|passwd|pass|token|access-token|auth-token|api-key|apikey|secret|client-secret|with-token))\s+(?<value>""[^""]*""|'[^']*'|[^\s""'-][^\s""']*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SecretFlagWithValue();

    // curl -u user:password / --user user:password.
    [GeneratedRegex(@"(?<flag>(?:^|(?<=\s))(?:-u|--user))\s+(?<user>[^\s:""']+):(?<value>[^\s""']+)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UserPassFlag();

    // Clients whose short -p/-P/-a flag carries a password (elsewhere -p is mkdir's "parents", dotnet's "project"…).
    [GeneratedRegex(@"\b(?:mysql\w*|mariadb\w*|mongo(?:sh|dump|restore|import|export)?|sshpass|sqlcmd|redis-cli|(?:docker|podman|nerdctl|helm)\s+(?:registry\s+)?login)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CredentialClient();

    [GeneratedRegex(@"(?<flag>(?:^|(?<=\s))-[pPa])(?:\s+|(?=[^\s-]))(?<value>""[^""]*""|'[^']*'|[^\s""'-][^\s""']*)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex ShortPasswordFlag();

    // Password=…; in a connection string, GITHUB_TOKEN=…, --api-key=…, "client_secret": "…". Not a URL's user part
    // ("https://x-access-token:…@host" — UrlCredentials keeps its host readable).
    [GeneratedRegex(@"(?<key>(?<![/\w.-])[A-Za-z0-9_.-]*(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|accountkey|sharedaccesskey|credentials?))(?<quote>[""']?)(?<sep>\s*[=:]\s*)(?<value>""[^""]*""|'[^']*'|[^\s;&|""',}]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex KeyValueSecret();

    // Token shapes that are secrets wherever they appear (case-sensitive prefixes).
    [GeneratedRegex(@"\b(?:sk-ant-[A-Za-z0-9_-]{8,}|sk-(?:proj-)?[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,}|glpat-[A-Za-z0-9_-]{16,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|npm_[A-Za-z0-9]{30,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex KnownToken();
}
