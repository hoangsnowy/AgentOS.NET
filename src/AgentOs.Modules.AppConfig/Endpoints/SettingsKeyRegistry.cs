// The runtime-settings contract for the /settings endpoints. POST /settings used to accept ANY
// key/value into the encrypted store — an admin typo created dead rows, and a bad provider name or
// endpoint only surfaced when the next pipeline run failed. The registry whitelists the keys the
// Settings UI actually owns and validates each value at save time.

using System;
using System.Collections.Frozen;
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
        // GAP-01: an Azure OpenAI resource host only — a tenant admin must not aim server-side calls at another host.
        [AppConfigKeys.LlmAzureEndpoint] = AgentOs.SharedKernel.Security.AzureOpenAiEndpoint.Validate,
        [AppConfigKeys.GithubPat] = ValidGitHubPat,
        [AppConfigKeys.GithubRepoOwner] = ValidGitHubName,
        [AppConfigKeys.GithubRepoName] = ValidGitHubName,
        [AppConfigKeys.GithubBaseBranch] = ValidBranchName,
    };

    public static bool IsReadablePrefix(string prefix) => ReadablePrefixes.Contains(prefix);

    // FAIL CLOSED (GAP-08 follow-up): GET /settings/{prefix} returns a value in clear ONLY when the key is known to be
    // non-secret — declared in the settings catalog with a non-Secret kind, or in this explicit list of keys the
    // registry owns but no module describes. Every other key (a legacy row, a pool key, a future Github:App:PrivateKey,
    // Llm:X:Key or …:ClientCert nobody has declared yet) is masked. A deny-list of secret-looking names used to be the
    // rule, and any credential it didn't anticipate came back in clear.
    private static readonly FrozenSet<string> PlainKeys = new[]
    {
        AppConfigKeys.LlmForceProvider,
        AppConfigKeys.LlmAzureEndpoint,
        AppConfigKeys.GithubRepoOwner,
        AppConfigKeys.GithubRepoName,
        AppConfigKeys.GithubBaseBranch,
        AppConfigKeys.GithubBaseUrl,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Defence in depth: a name that reads like a credential is masked even when a descriptor (mis)declares it non-secret.
    private static readonly string[] SecretSuffixes = ["ApiKey", "ApiKeys", ":Pat", "Token", "Secret", "Password", "PrivateKey"];

    /// <summary>True when <paramref name="key"/>'s value must be masked: everything except a key known to be
    /// non-secret (a non-<see cref="SettingKind.Secret"/> descriptor in the catalog, or an explicitly listed
    /// coordinate such as <c>Github:RepoOwner</c>). A key named like a credential is always masked.</summary>
    public static bool IsSecret(string key, ISettingsCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var suffix in SecretSuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        if (catalog is not null && catalog.TryGet(key, out var d)) { return d.IsSecret; }
        return !PlainKeys.Contains(key);
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
