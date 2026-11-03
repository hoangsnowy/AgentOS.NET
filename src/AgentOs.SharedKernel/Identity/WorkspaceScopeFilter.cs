// RBAC v2 (E1.2) — endpoint filter for routes that carry a board id: the tenant-scope policy already passed, this
// re-checks the permission ON THAT BOARD (a restricted board's memberships decide). Reads → 404 when the caller
// can't see the board (it doesn't exist for them); writes → 403.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.SharedKernel.Identity;

/// <summary>Board-scope permission checks for routes with a board id in the path.</summary>
public static class WorkspaceScopeFilter
{
    /// <summary>Require ViewWork on the route's board for GET/HEAD and <paramref name="write"/> otherwise.</summary>
    /// <param name="builder">The endpoint (group) builder.</param>
    /// <param name="routeKey">The route value holding the board id (e.g. <c>boardId</c>).</param>
    /// <param name="write">The permission non-read methods need on the board.</param>
    public static TBuilder RequireWorkspacePermission<TBuilder>(this TBuilder builder, string routeKey, Permission write)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (http.GetRouteValue(routeKey) is not { } raw || !Guid.TryParse(raw.ToString(), out var boardId))
            {
                return await next(ctx).ConfigureAwait(false);
            }
            var access = http.RequestServices.GetService<IWorkspaceAccess>();
            var tenant = http.RequestServices.GetService<ITenantContext>();
            if (access is null || tenant is null)
            {
                return await next(ctx).ConfigureAwait(false);
            }
            var map = await access.ForUserAsync(tenant.TenantId, http.User, http.RequestAborted).ConfigureAwait(false);
            if (!map.CanSee(boardId))
            {
                return Results.NotFound();
            }
            var isRead = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
            return isRead || map.Can(boardId, write) ? await next(ctx).ConfigureAwait(false) : Results.Forbid();
        });
        return builder;
    }
}
