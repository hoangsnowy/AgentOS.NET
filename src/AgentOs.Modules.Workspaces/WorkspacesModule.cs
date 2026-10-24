// M2 — Workspaces module entry. Registers the workspace repository + DbContext, mounts the
// /workspaces endpoints, and applies the EF migration at startup. When no DB is configured, the
// repo falls back to a no-op so the host still boots (same convention as PipelineModule).

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Workspaces.Endpoints;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Repositories;
using AgentOs.SharedKernel.Modularity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentOs.Modules.Workspaces;

public sealed class WorkspacesModule : IModule, IEndpointModule, IInitializableModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        // Defense-in-depth host allowlist for tenant-supplied workspace hosts (atop the connect-time
        // SsrfGuard). Defaults cover the provider public hosts; operators add GHE/ADO-Server hosts via
        // Workspaces:AllowedHosts.
        services.Configure<Security.WorkspaceHostOptions>(configuration.GetSection("Workspaces"));
        services.AddSingleton<Security.IWorkspaceHostPolicy, Security.WorkspaceHostPolicy>();
        services.AddSingleton<AgentOs.Domain.Configuration.ISettingsContributor, WorkspacesSettings>();

        // The connect flow is shared by the HTTP endpoint and the desktop Board app (tenant-explicit).
        services.AddScoped<IWorkspaceConnector, WorkspaceConnector>();
        // A board's repositories for agents that see the whole board (the Autopilot lead reads their practices).
        services.AddScoped<AgentOs.Domain.Workspaces.IBoardRepos, WorkspaceBoardRepos>();

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<WorkspacesDbContext>(opt =>
                opt.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "workspaces"))
                   .AddInterceptors(AgentOs.SharedKernel.Persistence.RowLevelSecurityInterceptor.Instance));
            services.AddNpgsqlConnectionFactory(connectionString);
            services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
            services.AddScoped<ITicketFlowStore, EfTicketFlowStore>();
        }
        else
        {
            var requireDb = configuration.GetValue("Persistence:RequireDatabase", true);
            if (requireDb)
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection is empty but Persistence:RequireDatabase is true. "
                    + "Run the AppHost (dotnet run --project infra/AgentOs.AppHost) so Aspire wires Postgres, "
                    + "or set Persistence:RequireDatabase=false to opt into the legacy no-op repositories.");
            }
            services.AddSingleton<IWorkspaceRepository, NullWorkspaceRepository>();
            // The standalone Web keeps ticket flows in memory, so the Workflow › Ticket flow editor saves and reverts.
            services.AddSingleton<ITicketFlowStore, InMemoryTicketFlowStore>();
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapWorkspaceEndpoints();
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetService<WorkspacesDbContext>();
        if (db is not null)
        {
            // Advisory lock: serialise concurrent replicas racing the same migration at boot.
            await using var migrationLock = await AgentOs.SharedKernel.Persistence.PgAdvisoryLock
                .AcquireAsync(db.Database.GetConnectionString(), "agentos:migrate:workspaces", ct).ConfigureAwait(false);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
    }
}
