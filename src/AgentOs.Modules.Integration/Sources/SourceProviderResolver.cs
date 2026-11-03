// M2 — resolves the right ISourceProvider for a SourceProviderKind from the DI-registered set.
// Mirrors how LlmClientFactory selects a provider by name; here we select by kind. E1.8: with a service scope factory
// (the DI path), each CODE host (GitHub, Azure DevOps) comes back wrapped in TrackerRoutingSourceProvider, so a board
// whose tickets live in Azure DevOps Boards or Jira reads and writes them there without any caller knowing.

using System;
using System.Collections.Generic;
using AgentOs.Domain.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.Integration.Sources;

internal sealed class SourceProviderResolver : ISourceProviderResolver
{
    private readonly Dictionary<SourceProviderKind, ISourceProvider> _byKind = new();

    public SourceProviderResolver(IEnumerable<ISourceProvider> providers)
        : this(providers, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public SourceProviderResolver(IEnumerable<ISourceProvider> providers, IServiceScopeFactory? scopes, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        foreach (var p in providers)
        {
            _byKind[p.Kind] = p; // last registration wins
        }
        if (scopes is null) { return; }
        var trackers = new Dictionary<SourceProviderKind, ISourceProvider>(_byKind);
        foreach (var kind in new[] { SourceProviderKind.GitHub, SourceProviderKind.AzureDevOps })
        {
            if (_byKind.TryGetValue(kind, out var host)) { _byKind[kind] = new TrackerRoutingSourceProvider(host, trackers, scopes, clock); }
        }
    }

    public ISourceProvider Resolve(SourceProviderKind kind)
        => _byKind.TryGetValue(kind, out var p)
            ? p
            : throw new InvalidOperationException($"No source provider registered for '{kind}'.");

    public bool TryResolve(SourceProviderKind kind, out ISourceProvider? provider)
        => _byKind.TryGetValue(kind, out provider);
}
