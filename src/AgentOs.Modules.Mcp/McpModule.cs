// Epic E3 — MCP module entry. Binds McpOptions, registers McpClientHost as a singleton, and in
// the IInitializableModule phase connects to every configured server and pumps its tools into the
// IToolRegistry. Depends on IToolRegistry being registered first (ToolsModule).

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Mcp.Configuration;
using AgentOs.SharedKernel.Modularity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgentOs.SharedKernel.Identity;

namespace AgentOs.Modules.Mcp;

public sealed class McpModule : IModule, IEndpointModule, IInitializableModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Fail-fast config: a server entry missing its name or transport coordinates would otherwise
        // surface as an opaque connect failure at startup (or a silently dead toolset).
        services.AddOptions<McpOptions>()
            .Bind(configuration.GetSection(McpOptions.SectionName))
            .Validate(o => o.CallTimeoutSeconds > 0,
                "Mcp:CallTimeoutSeconds must be > 0.")
            .Validate(o => o.Servers.All(s => !s.Enabled || !string.IsNullOrWhiteSpace(s.Name)),
                "Mcp:Servers — every enabled server needs a Name (it prefixes its tools).")
            .Validate(o => o.Servers.All(s => !s.Enabled
                    || !string.Equals(s.Transport, "stdio", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(s.Command)),
                "Mcp:Servers — a stdio server needs a Command to spawn.")
            .Validate(o => o.Servers.All(s => !s.Enabled
                    || !string.Equals(s.Transport, "http", StringComparison.OrdinalIgnoreCase)
                    || Uri.TryCreate(s.Url, UriKind.Absolute, out _)),
                "Mcp:Servers — an http server needs an absolute Url.")
            .ValidateOnStart();

        services.AddSingleton<McpClientHost>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Read-only admin surface: which configured upstream servers connected at startup.
        // Runtime CRUD is deliberately out of scope (config is appsettings-bound; edit + restart).
        endpoints.MapGet("/mcp/servers", (McpClientHost host) => Results.Ok(host.Statuses))
            .WithName("McpServers")
            .WithSummary("Configured upstream MCP servers + startup connection status")
            .WithTags("Mcp")
            .RequirePermission(Permission.ManageSettings);
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var host = services.GetRequiredService<McpClientHost>();
        await host.ConnectAllAsync(ct).ConfigureAwait(false);
    }
}
