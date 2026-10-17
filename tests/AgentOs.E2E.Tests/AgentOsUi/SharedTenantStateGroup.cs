// Real-auth tests that temporarily change SHARED tenant state other tests depend on (the budget test seeds spend
// over the cap for tenant `default`) must not overlap the rest of the suite: xUnit runs test classes in parallel,
// and a Workflow run started while the cap is exceeded is (correctly) blocked. Members of this collection run alone.

using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

/// <summary>Serial collection for tests that mutate shared tenant state.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedTenantStateGroup
{
    public const string Name = "Shared tenant state (serial)";
}
