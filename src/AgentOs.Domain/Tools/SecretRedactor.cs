// AgentOs.Domain/Tools/SecretRedactor.cs
// Governance of tool calls made on a member's computer — the one rule for what may leave it as evidence. The runner
// summarises every tool call its CLI made (the Bash command, the edited file's path) and the server records it; both
// sides apply THIS function (the runner links this file, BCL-only), so a summary the runner redacted is re-checked with
// exactly the same rules before it is stored. A summary is the FIRST line only (a heredoc's body is file content),
// credential-shaped text is replaced by "[redacted]", and the result is bounded (500 characters by default).
//
// Input is bounded BEFORE any pattern runs: only the first 4 KB are scanned (cut back to a word boundary so a secret
// split by the cut leaves no readable prefix), and the rest is replaced by a "[redacted: N more characters]" note.
//
// Redacted:
//   • headers — Authorization / Proxy-Authorization / API-key / cookie headers, Bearer and Basic values;
//   • well-known token shapes — Anthropic/OpenAI sk-…, GitHub ghp_/gho_/ghu_/ghs_/ghr_/github_pat_, GitLab, Slack, AWS
//     access keys, Google API keys, npm, JWTs (an unsigned one too);
//   • URLs — the password of user:password@host, a colon-less user of 16+ token characters (an Azure DevOps PAT as
//     https://<pat>@dev.azure.com; a short user such as git@ or myorg@ stays readable), and the secret query values
//     sig= / signature= / code= / token= / key= / X-Amz-Signature= … (Azure SAS, OAuth codes, presigned URLs);
//   • assignments — any name ending in password / secret / token / key / credential(s) / pat (SECRET_KEY=…,
//     export OPENAI_KEY=…, STRIPE_KEY=…, Password=…; in a connection string, "client_secret": "…", YAML token: x);
//   • flags — --password x, --token x, --client-secret x, --account-key x, --github-token x (any --…-password,
//     --…-token, --…-secret, --…-key, --…-pat), curl's -u user:pass, a short -p/-P/-a on credential-taking clients
//     (mysql -psecret, sshpass -p x, sqlcmd -P x, redis-cli -a x, docker/podman/helm login -p x, az/oc/cf login -p x);
//   • positional values after secret-setting subcommands — dotnet user-secrets set <key> <value>, gh/az … secret set
//     … --body/-b/--value <value>, aws configure set <*secret*|*key*|*token*|*password*> <value>, htpasswd -b [file]
//     user <password>, and openssl's pass:<password> argument;
//   • private-key blocks;
//   • a generic fallback: a run of 32+ hex / base64url characters that looks random (hex with digits and letters;
//     mixed-case base64 with digits; a lowercase or uppercase base32-style token with digits). A canonical GUID stays,
//     and so does an identifier-like run with many - or _ separators.
//
// Deliberately NOT redacted: a commit SHA (40/64 hex) right after git checkout|show|log|revert|cherry-pick|reset|diff|
// switch (evidence of what the agent looked at, never a credential), and a content digest after sha1:/sha256:/sha512:
// (docker image@sha256:…, npm integrity). Any other long hex run IS masked — a SHA elsewhere is the price of never
// letting a hex secret (an API key, a webhook secret) through.
//
// A pattern that times out redacts the whole text (fail closed).

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentOs.Domain.Tools;

/// <summary>Redacts credential-shaped text and bounds a one-line summary of a tool call's input.</summary>
public static partial class SecretRedactor
{
    /// <summary>What a secret is replaced by.</summary>
    public const string Mask = "[redacted]";

    /// <summary>The longest summary (characters), the ellipsis included.</summary>
    public const int DefaultMaxLength = 500;

    /// <summary>The most characters the patterns ever scan; anything after them is replaced by a note.</summary>
    public const int MaxScanLength = 4096;

    private const int TimeoutMs = 250;

    /// <summary>Replace every credential-shaped part of <paramref name="text"/> by <see cref="Mask"/>. Only the first
    /// <see cref="MaxScanLength"/> characters are kept (cut back to a word boundary); the rest is masked.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return string.Empty; }
        var (scan, dropped) = Cap(text.AsSpan(), text);
        return RedactCore(scan, dropped);
    }

    private static string RedactCore(string scan, int dropped)
    {
        try
        {
            var s = PrivateKey().Replace(scan, "[redacted private key]");
            s = UrlCredentials().Replace(s, m => m.Groups["scheme"].Value + m.Groups["user"].Value + ":" + Mask + "@");
            s = UrlTokenUser().Replace(s, m => m.Groups["scheme"].Value + Mask + "@");
            s = SecretQueryValue().Replace(s, m => m.Groups["key"].Value + "=" + Mask);
            s = SecretHeader().Replace(s, m => m.Groups["name"].Value + ": " + Mask);
            s = AuthScheme().Replace(s, m => m.Groups["scheme"].Value + " " + Mask);
            s = UserSecretsSet().Replace(s, m => m.Groups["lead"].Value + Mask);
            s = SecretSetBody().Replace(s, m => m.Groups["lead"].Value + m.Groups["sep"].Value + Mask);
            s = ConfigureSet().Replace(s, m => m.Groups["lead"].Value + Mask);
            s = Htpasswd().Replace(s, m => m.Groups["lead"].Value + Mask);
            s = OpensslPass().Replace(s, "pass:" + Mask);
            s = SecretFlagWithValue().Replace(s, m => m.Groups["flag"].Value + " " + Mask);
            s = UserPassFlag().Replace(s, m => m.Groups["flag"].Value + " " + m.Groups["user"].Value + ":" + Mask);
            if (CredentialClient().IsMatch(s))
            {
                s = ShortPasswordFlag().Replace(s, m => m.Groups["flag"].Value + " " + Mask);
            }
            s = KeyValueSecret().Replace(s, m => m.Groups["key"].Value + m.Groups["quote"].Value + m.Groups["sep"].Value + Mask);
            s = KnownToken().Replace(s, Mask);
            s = HighEntropyRun().Replace(s, m => LooksRandom(m.ValueSpan) ? Mask : m.Value);
            return dropped == 0
                ? s
                : string.Concat(s, " [redacted: ", dropped.ToString(CultureInfo.InvariantCulture), " more characters]");
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
        // One pass over a span: no Split of a (possibly huge) text into every line.
        var rest = text.AsSpan().Trim();
        var newline = rest.IndexOf('\n');
        var first = (newline < 0 ? rest : rest[..newline]).TrimEnd('\r').Trim();
        var more = newline < 0 ? 0 : rest[(newline + 1)..].Count('\n') + 1;
        var (scan, dropped) = Cap(first, null);
        var summary = scan.Length == 0 ? string.Empty : RedactCore(scan, dropped);
        if (more > 0)
        {
            summary += more == 1 ? " (+1 more line)" : $" (+{more.ToString(CultureInfo.InvariantCulture)} more lines)";
        }
        return Bound(summary, maxLength);
    }

    /// <summary>Cut <paramref name="text"/> to <paramref name="maxLength"/> characters (an ellipsis marks the cut; a
    /// surrogate pair is never split). Control characters become spaces, so a summary is always one line.</summary>
    public static string Bound(string? text, int maxLength = DefaultMaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        if (string.IsNullOrEmpty(text)) { return string.Empty; }
        var length = Math.Min(text.Length, maxLength);
        var cut = text.Length > maxLength;
        if (cut)
        {
            length = maxLength - 1;
            if (char.IsHighSurrogate(text[length - 1])) { length--; }
        }
        return string.Create(length + (cut ? 1 : 0), (text, length, cut), static (dest, st) =>
        {
            for (var i = 0; i < st.length; i++)
            {
                var ch = st.text[i];
                dest[i] = char.IsControl(ch) ? ' ' : ch;
            }
            if (st.cut) { dest[^1] = '…'; }
        });
    }

    // The part the patterns scan: at most MaxScanLength characters, cut back to the last whitespace (within 256
    // characters) so a secret the cut splits never leaves a readable prefix that no longer matches its pattern.
    // Only the kept part is ever copied: a multi-megabyte line costs 4 KB, not a copy of itself.
    private static (string Scan, int Dropped) Cap(ReadOnlySpan<char> text, string? whole)
    {
        if (text.Length <= MaxScanLength) { return (whole ?? text.ToString(), 0); }
        var ws = text[(MaxScanLength - 256)..MaxScanLength].LastIndexOfAny(' ', '\t', '\n');
        var keep = ws < 0 ? MaxScanLength : MaxScanLength - 256 + ws;
        return (text[..keep].ToString(), text.Length - keep);
    }

    // The generic fallback: does a 32+ run of hex / base64url characters look like a random secret? One pass counts the
    // character classes, the separate groups of digits (random text scatters them; a name keeps them together: a
    // timestamp, a version) and the case flips between adjacent letters (≈ half of them in random text, few in
    // CamelCase words).
    internal static bool LooksRandom(ReadOnlySpan<char> run)
    {
        var body = run.TrimEnd('=');
        int upper = 0, lower = 0, digit = 0, sep = 0, beyondHex = 0, digitGroups = 0, letterPairs = 0, flips = 0;
        var prev = 0; // 0 = not a letter/digit, 1 = lower, 2 = upper, 3 = digit
        foreach (var ch in body)
        {
            int kind;
            if (char.IsAsciiDigit(ch)) { digit++; kind = 3; if (prev != 3) { digitGroups++; } }
            else if (char.IsAsciiLetterUpper(ch)) { upper++; kind = 2; if (ch > 'F') { beyondHex++; } }
            else if (char.IsAsciiLetterLower(ch)) { lower++; kind = 1; if (ch > 'f') { beyondHex++; } }
            else { sep++; kind = 0; }
            if (kind is 1 or 2 && prev is 1 or 2)
            {
                letterPairs++;
                if (kind != prev) { flips++; }
            }
            prev = kind;
        }
        if (sep == 0 && beyondHex == 0 && digit > 0 && upper + lower > 0 && (upper == 0 || lower == 0))
        {
            return true; // hex (one case, digits + letters): an API key, a webhook secret, an unexplained SHA
        }
        if (IsGuid(body) || sep * 6 > body.Length)
        {
            return false; // a GUID, or an identifier (kebab-/snake-case words)
        }
        if (upper >= 2 && lower >= 2 && digit >= 2)
        {
            // mixed-case base64 / base64url — not a CamelCase name with a date in it (20261101_MemberComputerEvidence)
            return digitGroups >= 3 || flips * 10 >= letterPairs * 3;
        }
        return digitGroups >= 3 && beyondHex > 0
            && ((upper == 0 && digit >= 4 && lower >= 8) // lowercase base32 (an Azure DevOps PAT)
                || (lower == 0 && digit >= 4 && upper >= 8)); // uppercase base32 (a TOTP seed)
    }

    private static bool IsGuid(ReadOnlySpan<char> s) =>
        s.Length == 36 && s[8] == '-' && s[13] == '-' && s[18] == '-' && s[23] == '-' && Guid.TryParseExact(s, "D", out _);

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PrivateKey();

    // scheme://user:password@host — the user stays, the password goes.
    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]{1,15}://)(?<user>[^\s:/@""']+):(?<pass>[^\s@/""']+)@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UrlCredentials();

    // scheme://<token>@host with no colon: a 16+ character token-like user is a credential (an Azure DevOps PAT); a
    // short user (git@, myorg@) stays readable.
    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]{1,15}://)(?<user>[A-Za-z0-9_.~+=-]{16,})@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UrlTokenUser();

    // A URL's secret query values: Azure SAS sig=, an OAuth code=, token=, key=, AWS/GCS presigned signatures.
    [GeneratedRegex(@"(?<=[?&;])(?<key>sig|signature|code|key|apikey|api_key|api-key|secret|password|pwd|auth|[A-Za-z0-9_.-]*token|x-amz-signature|x-amz-credential|x-amz-security-token|x-goog-signature|x-goog-credential)=(?<value>[^&\s""'#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SecretQueryValue();

    // A header name only where a header starts (line start, after a space, a quote or a brace) — never "x-access-token:" as
    // the user part of a URL, which UrlCredentials already handled (and whose host must stay readable).
    [GeneratedRegex(@"(?:^|(?<=[\s""'{,]))(?<name>proxy-authorization|authorization|x-api-key|api-key|x-auth-token|x-access-token|private-token|set-cookie|cookie)\s*:\s*(?<value>[^""'\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SecretHeader();

    [GeneratedRegex(@"\b(?<scheme>bearer|basic)\s+(?<value>[A-Za-z0-9._~+/=-]{8,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex AuthScheme();

    // dotnet user-secrets set [--project x|--id x|-p x] <key> <value>: the value is the secret.
    [GeneratedRegex(@"(?<lead>\buser-secrets\s+set\s+(?:(?:--project|--id|--configuration|-p|-c)\s+\S+\s+)*(?:""[^""]*""|'[^']*'|[^\s""'-][^\s""']*)\s+)(?<value>""[^""]*""|'[^']*'|[^\s""']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UserSecretsSet();

    // gh secret set NAME --body x / -b x / -bx, az keyvault secret set … --value x.
    [GeneratedRegex(@"(?<lead>\bsecret\s+set\b[^\r\n]*?\s(?:--body|--value|-b))(?<sep>\s+|=|(?=[^\s=-]))(?<value>""[^""]*""|'[^']*'|[^\s""']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SecretSetBody();

    // aws configure set aws_secret_access_key x (any setting naming a secret, key, token or password).
    [GeneratedRegex(@"(?<lead>\bconfigure\s+set\s+[\w.-]*(?:secret|key|token|password)[\w.-]*\s+)(?<value>""[^""]*""|'[^']*'|[^\s""']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex ConfigureSet();

    // htpasswd -b[c…] file user password, htpasswd -nb user password: the last positional value is the password.
    [GeneratedRegex(@"(?<lead>\bhtpasswd(?:\s+-[A-Za-z]+)*?\s+-[A-Za-z]*b[A-Za-z]*(?:\s+-[A-Za-z]+)*\s+(?:[^\s<>|;&-][^\s<>|;&]*\s+)?[^\s<>|;&-][^\s<>|;&]*\s+)(?<value>""[^""]*""|'[^']*'|[^\s<>|;&]+)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Htpasswd();

    // openssl's passphrase argument: -passin pass:x, -passout pass:x, -pass pass:x (env:/file:/fd: name a source, not a secret).
    [GeneratedRegex(@"\bpass:(?<value>""[^""]*""|'[^']*'|[^\s""']+)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex OpensslPass();

    // --password value, --token "value", --client-secret x, --account-key x, --github-token x (the "=" forms are KeyValueSecret's).
    [GeneratedRegex(@"(?<flag>--(?:[A-Za-z0-9]+-)*(?:password|passwd|pass|token|secret|key|apikey|pat))\s+(?<value>""[^""]*""|'[^']*'|[^\s""'-][^\s""']*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SecretFlagWithValue();

    // curl -u user:password / --user user:password.
    [GeneratedRegex(@"(?<flag>(?:^|(?<=\s))(?:-u|--user))\s+(?<user>[^\s:""']+):(?<value>[^\s""']+)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UserPassFlag();

    // Clients whose short -p/-P/-a flag carries a password (elsewhere -p is mkdir's "parents", dotnet's "project",
    // psql's "port"…).
    [GeneratedRegex(@"\b(?:mysql\w*|mariadb\w*|mongo(?:sh|dump|restore|import|export)?|sshpass|sqlcmd|redis-cli|(?:docker|podman|nerdctl|helm)\s+(?:registry\s+)?login|(?:az|oc|cf)\s+(?:[a-z]+\s+)?login)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CredentialClient();

    [GeneratedRegex(@"(?<flag>(?:^|(?<=\s))-[pPa])(?:\s+|(?=[^\s-]))(?<value>""[^""]*""|'[^']*'|[^\s""'-][^\s""']*)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex ShortPasswordFlag();

    // Password=…; in a connection string, GITHUB_TOKEN=…, SECRET_KEY=…, OPENAI_KEY=…, --api-key=…, "client_secret": "…",
    // YAML token: x, AZURE_DEVOPS_EXT_PAT=…. Not a URL's user part ("https://x-access-token:…@host" — UrlCredentials keeps
    // its host readable).
    [GeneratedRegex(@"(?<key>(?<![/\w.-])(?:[A-Za-z0-9_.-]*(?:password|passwd|pwd|secret|token|key|credentials?)|(?:[A-Za-z0-9_.-]*_)?pat))(?<quote>[""']?)(?<sep>\s*[=:]\s*)(?!\[redacted)(?<value>""[^""]*""|'[^']*'|[^\s;&|""',}]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex KeyValueSecret();

    // Token shapes that are secrets wherever they appear (case-sensitive prefixes). A JWT's signature may be empty (alg none).
    [GeneratedRegex(@"\b(?:sk-ant-[A-Za-z0-9_-]{8,}|sk-(?:proj-)?[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,}|glpat-[A-Za-z0-9_-]{16,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|npm_[A-Za-z0-9]{30,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex KnownToken();

    // A candidate for the generic fallback: 32+ hex / base64url characters (optional = padding), not a SHA right after a
    // git command that names a commit, nor a content digest (sha256:…). LooksRandom decides.
    [GeneratedRegex(@"(?<![A-Za-z0-9_+-])(?<!\bgit\s+(?:checkout|show|log|revert|cherry-pick|reset|diff|switch)\s+(?:-\S*\s+)*)(?<!\bsha(?:1|256|384|512)[:=-])[A-Za-z0-9_+-]{32,}={0,2}(?![A-Za-z0-9_+=-])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex HighEntropyRun();
}
