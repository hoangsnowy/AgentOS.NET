// M2 — the source-control providers AgentOS.NET can connect a workspace to. The provider abstraction
// (ISourceProvider) is keyed by this so GitHub and Azure DevOps live behind one seam, the way
// ILlmClient sits in front of the LLM vendors.

namespace AgentOs.Domain.Workspaces;

/// <summary>A source-control backend a <see cref="WorkspaceDescriptor"/> can be connected to.</summary>
public enum SourceProviderKind
{
    /// <summary>github.com or GitHub Enterprise.</summary>
    GitHub = 0,

    /// <summary>Azure DevOps (dev.azure.com / on-prem TFS).</summary>
    AzureDevOps = 1,
}
