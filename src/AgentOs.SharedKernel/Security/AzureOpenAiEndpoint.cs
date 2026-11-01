// GAP-01 (docs/security/threat-model.md §15) — the Azure OpenAI endpoint is a TENANT-scoped setting, and the server calls
// it with the tenant's key. Validated only as "absolute https://", a tenant admin could point it at any host — an
// internal service, the cloud metadata address — and have the server send requests there (SSRF).
// Two layers, both for a value that came from Settings (a tenant or platform admin, at runtime):
//   • the host must be an Azure OpenAI / Azure AI resource host (allowlist below), on the default https port — checked
//     when the value is saved AND again when it is used (a value stored before this check is refused at call time);
//   • the HTTP call goes through SsrfGuard's connect-time handler, so a name that resolves to a private address is
//     refused too.
// The bootstrap endpoint (Llm:AzureOpenAi:Endpoint from env / user-secrets / Key Vault — operator-controlled) stays
// trusted and unrestricted: a Private Link endpoint legitimately resolves to a private address.

using System;

namespace AgentOs.SharedKernel.Security;

/// <summary>The allowlist for a runtime-configured Azure OpenAI endpoint.</summary>
public static class AzureOpenAiEndpoint
{
    /// <summary>Host suffixes of Azure OpenAI / Azure AI resources (public, US Government and China clouds).</summary>
    public static readonly string[] AllowedHostSuffixes =
    [
        ".openai.azure.com", ".cognitiveservices.azure.com", ".services.ai.azure.com",
        ".openai.azure.us", ".cognitiveservices.azure.us",
        ".openai.azure.cn", ".cognitiveservices.azure.cn",
    ];

    /// <summary>Null when <paramref name="value"/> is an allowed endpoint, else the user-facing reason.</summary>
    public static string? Validate(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "Endpoint must be an absolute https:// URL.";
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.HostNameType != UriHostNameType.Dns)
        {
            return "Endpoint must be an Azure OpenAI resource URL like https://<resource>.openai.azure.com (no IP, port or credentials).";
        }
        foreach (var suffix in AllowedHostSuffixes)
        {
            // The resource name must be present: ".openai.azure.com" itself is not a resource.
            if (uri.Host.Length > suffix.Length && uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        return "Endpoint must be an Azure OpenAI resource (https://<resource>.openai.azure.com, .cognitiveservices.azure.com or "
            + ".services.ai.azure.com). Other hosts can only be configured by the operator (Llm:AzureOpenAi:Endpoint).";
    }
}
