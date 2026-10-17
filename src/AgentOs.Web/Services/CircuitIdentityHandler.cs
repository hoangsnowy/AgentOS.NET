// Pushes the signed-in user's tenant as the AmbientIdentity for EVERY inbound activity of a Blazor circuit (UI
// events, JS interop callbacks). A circuit has no HttpContext, so without this every database call from the
// desktop ran with no known tenant: row-level security (ADR-0012) couldn't bind it, and stores that resolve the
// tenant ambiently had to be fed it by hand. Background work started from an event inherits it (ExecutionContext).

using System;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace AgentOs.Web.Services;

/// <summary>Scoped (one per circuit): wraps each inbound activity in <see cref="AmbientIdentity.Push(string, string?)"/>.</summary>
internal sealed class CircuitIdentityHandler(AuthenticationStateProvider auth) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next)
        => async context =>
        {
            // ConfigureAwait(true): the inbound activity MUST continue on the circuit's dispatcher (renderer thread affinity).
            var state = await auth.GetAuthenticationStateAsync().ConfigureAwait(true);
            var user = state.User;
            using var _ = user.Identity?.IsAuthenticated == true && user.GetTenantId() is { Length: > 0 } tenant
                ? AmbientIdentity.PushUser(tenant, user.GetUserId(), user.Identity?.Name)
                : null;
            await next(context).ConfigureAwait(true);
        };
}
