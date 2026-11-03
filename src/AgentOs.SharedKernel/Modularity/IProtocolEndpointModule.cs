// A module whose endpoints are a wire protocol with its own versioning (a SignalR hub), not REST. The host's versioned
// mapping (ModuleLoader.MapVersionedModuleEndpoints) maps these once, at their own path — never under /v1 and never
// marked deprecated, so a paired runner's hub URL keeps working unchanged.

namespace AgentOs.SharedKernel.Modularity;

/// <summary>An endpoint module serving a protocol endpoint (e.g. a SignalR hub) that is not REST-versioned.</summary>
public interface IProtocolEndpointModule : IEndpointModule;
