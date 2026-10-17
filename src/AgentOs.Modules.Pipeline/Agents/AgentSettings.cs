// Agent profiles declared for the UI-first registry (ADR-0011). One profile per agent role —
// provider, model, temperature, max output tokens — plus the pipeline's QA-loop cap. Defaults come from
// AgentsOptions / PipelineOptions so the registry and the code defaults cannot drift. Keys match the
// existing appsettings paths, so current deployments seed straight in.

using System.Collections.Generic;
using System.Globalization;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Pipeline.Pipeline;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Declares <c>Agents:&lt;Role&gt;:*</c> and <c>Pipeline:*</c> settings.</summary>
public sealed class AgentSettings : ISettingsContributor
{
    /// <summary>UI category for agent profiles.</summary>
    public const string Category = "Agent profiles";

    /// <summary>UI category for pipeline behaviour.</summary>
    public const string PipelineCategory = "Pipeline";

    /// <summary>QA-loop iteration cap.</summary>
    public const string MaxIterations = "Pipeline:MaxIterations";

    /// <summary>Largest output any current model accepts (Claude 5.x / GPT long-context: 128K). The per-model
    /// limit is enforced against the model catalog; this is the absolute ceiling.</summary>
    public const int MaxOutputTokensCeiling = 128_000;

    /// <summary>Agent roles in pipeline order, paired with their code defaults.</summary>
    public static IReadOnlyList<(string Role, AgentOptions Defaults)> Roles { get; } = BuildRoles();

    /// <summary>Setting key for <paramref name="role"/>'s <paramref name="field"/> (Provider/Model/Temperature/MaxTokens).</summary>
    public static string Key(string role, string field) => $"{AgentsOptions.SectionName}:{role}:{field}";

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        foreach (var (role, d) in Roles)
        {
            yield return new(Key(role, nameof(AgentOptions.Provider)), SettingKind.Choice, Category, $"{role} · provider")
            {
                DefaultValue = d.Provider,
                Choices = LlmProviders.Selectable,
            };
            yield return new(Key(role, nameof(AgentOptions.Model)), SettingKind.Model, Category, $"{role} · model")
            {
                DefaultValue = d.Model,
            };
            yield return new(Key(role, nameof(AgentOptions.Temperature)), SettingKind.Number, Category, $"{role} · temperature")
            {
                DefaultValue = d.Temperature.ToString(CultureInfo.InvariantCulture),
                Min = 0m,
                Max = 2m,
                Help = "Ignored by models that reject sampling parameters (Claude 4.7+/5.x, GPT-5+).",
            };
            yield return new(Key(role, nameof(AgentOptions.MaxTokens)), SettingKind.WholeNumber, Category, $"{role} · max output tokens")
            {
                DefaultValue = d.MaxTokens.ToString(CultureInfo.InvariantCulture),
                Min = 1m,
                Max = MaxOutputTokensCeiling,
                Help = "Upper bound on the reply. Adaptive-thinking models spend part of it on reasoning.",
            };
        }

        yield return new(MaxIterations, SettingKind.WholeNumber, PipelineCategory, "QA loop · max iterations")
        {
            DefaultValue = new PipelineOptions().MaxIterations.ToString(CultureInfo.InvariantCulture),
            Min = 1m,
            Max = 10m,
            Help = "How many Coding → Testing → QA rounds before the run stops.",
        };
    }

    private static List<(string, AgentOptions)> BuildRoles()
    {
        var o = new AgentsOptions();
        return
        [
            (nameof(AgentsOptions.Orchestrator), o.Orchestrator),
            (nameof(AgentsOptions.Requirement), o.Requirement),
            (nameof(AgentsOptions.Coding), o.Coding),
            (nameof(AgentsOptions.Testing), o.Testing),
            (nameof(AgentsOptions.Qa), o.Qa),
            (nameof(AgentsOptions.IssueWork), o.IssueWork),
            (nameof(AgentsOptions.Decomposer), o.Decomposer),
        ];
    }
}
