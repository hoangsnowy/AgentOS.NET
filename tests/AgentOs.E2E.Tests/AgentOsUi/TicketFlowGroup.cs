// Tests that edit the tenant's ticket flow (Workflow › Ticket flow — one shared flow for tenant `default`) run one after
// another: a save in one window reloads the other's view mid-edit. Members of this collection never overlap each other;
// the rest of the suite still runs in parallel with them.

using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

/// <summary>Serial collection for tests that change the shared ticket flow.</summary>
[CollectionDefinition(Name)]
public sealed class TicketFlowGroup
{
    public const string Name = "Ticket flow (serial)";
}
