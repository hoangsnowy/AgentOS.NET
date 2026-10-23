// "No model chosen" in an agent profile (the Board agents' default) means: let whoever serves the call pick. A member's
// paired CLI runs its own default model; a server provider runs the model configured for it (Settings → Model keys /
// Llm:<Provider>:Model). Each server client resolves the empty model here, BEFORE LlmRequest.Validate, so a profile
// left on its default never throws on our servers.

using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Llm;

/// <summary>Fills an empty request model with the serving provider's configured model.</summary>
public static class ProviderDefaultModel
{
    /// <summary><paramref name="request"/> unchanged when it names a model (or no default is known); otherwise the same
    /// request with <see cref="LlmRequest.Model"/> = <paramref name="providerDefault"/>.</summary>
    public static LlmRequest Apply(LlmRequest request, string? providerDefault)
    {
        System.ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrWhiteSpace(request.Model) && !string.IsNullOrWhiteSpace(providerDefault)
            ? request with { Model = providerDefault.Trim() }
            : request;
    }
}
