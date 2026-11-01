// The runtime-settings contract for the /settings endpoints. POST /settings used to accept ANY
// key/value into the encrypted store — an admin typo created dead rows, and a bad provider name or
// endpoint only surfaced when the next pipeline run failed. The registry whitelists the keys the
// Settings UI actually owns and validates each value at save time.

using System;
using System.Collections.Generic;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.AppConfig.Endpoints;

internal static class SettingsKeyRegistry
{
    /// <summary>Prefixes <c>GET /settings/{prefix}</c> may enumerate.</summary>
    private static readonly HashSet<string> ReadablePrefixes =
        new(StringComparer.OrdinalIgnoreCase) { "Llm", "Github" };

    /// <summary>Writable keys → value validator (null error = valid). Empty values are always
    /// allowed — they clear the override back to the appsettings/platform fallback.</summary>
    private static readonly Dictionary<string, Func<string, string?>> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        [AppConfigKeys.LlmForceProvider] = ValidProvider,
        [AppConfigKeys.LlmClaudeApiKey] = _ => null,
        [AppConfigKeys.LlmAzureApiKey] = _ => null,
        [AppConfigKeys.LlmAzureEndpoint] = ValidHttpsUrl,
        [AppConfigKeys.GithubPat] = ValidGitHubPat,
        [AppConfigKeys.GithubRepoOwner] = ValidGitHubName,
        [AppConfigKeys.GithubRepoName] = ValidGitHubName,
        [AppConfigKeys.GithubBaseBranch] = ValidBranchName,
    };

    public static bool IsReadablePrefix(string prefix) => ReadablePrefixes.Contains(prefix);

    // Key-name suffixes that always mark a secret, whatever the catalog says — a key no module declares (a legacy
    // row, a pool key) is still never returned in clear (GAP-08).
    private static readonly string[] SecretSuffixes = ["ApiKey", "ApiKeys", ":Pat", "Token", "Secret", "Password"];

    /// <summary>True when <paramref name="key"/> holds a secret: declared <see cref="SettingKind.Secret"/> in the settings
    /// catalog, or named like one (<c>…ApiKey</c>, <c>…:Pat</c>, <c>…Token</c>, <c>…Secret</c>, <c>…Password</c>).</summary>
    public static bool IsSecret(string key, ISettingsCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (catalog is not null && catalog.TryGet(key, out var d) && d.IsSecret) { return true; }
        foreach (var suffix in SecretSuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    public static bool IsKnownKey(string key) => Keys.ContainsKey(key);

    /// <summary>Validates a value for a known key. Returns the error message, or null when valid.</summary>
    public static string? ValidateValue(string key, string value)
    {
        if (!Keys.TryGetValue(key, out var validator))
        {
            return $"'{key}' is not a recognised runtime setting.";
        }
        return string.IsNullOrWhiteSpace(value) ? null : validator(value);
    }

    private static string? ValidProvider(string value) =>
        value is "Claude" or "AzureOpenAI" or "MAF" or "RemoteAgent" or "Anthropic"
            ? null
            : "ForceProvider must be one of: Claude (alias: Anthropic), AzureOpenAI, MAF, RemoteAgent.";

    private static string? ValidHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? null
            : "Endpoint must be an absolute https:// URL.";

    private static string? ValidGitHubPat(string value) =>
        value.StartsWith("ghp_", StringComparison.Ordinal)
        || value.StartsWith("github_pat_", StringComparison.Ordinal)
        || value.StartsWith("gho_", StringComparison.Ordinal)
            ? null
            : "GitHub token must start with ghp_, github_pat_ or gho_.";

    private static string? ValidGitHubName(string value) =>
        value.Length <= 100 && value.AsSpan().IndexOfAnyExcept(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.".AsSpan()) < 0
            ? null
            : "Only letters, digits, '-', '_' and '.' are allowed.";

    private static string? ValidBranchName(string value) =>
        value.Length <= 250 && !value.Contains(' ', StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal)
            ? null
            : "Branch name must not contain spaces or '..'.";
}
