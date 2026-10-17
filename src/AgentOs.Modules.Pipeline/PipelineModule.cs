// Module entry: registers the agents, prompts, validation, metrics, orchestrator, pipeline client,
// repositories, and DbContext. Mounts /pipeline* + /runs* endpoints. Applies the EF migration at
// startup. When no DB is configured, repos fall back to no-ops so the host still boots.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Endpoints;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Pipeline.Persistence.Repositories;
using AgentOs.Modules.Pipeline.Pipeline;
using AgentOs.Modules.Pipeline.Validation;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Modularity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentOs.Modules.Pipeline;

public sealed class PipelineModule : IModule, IEndpointModule, IInitializableModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddValidation();

        var csvPath = configuration["Metrics:CsvPath"];
        if (!string.IsNullOrWhiteSpace(csvPath))
        {
            services.AddCsvMetrics(csvPath);
        }
        else
        {
            services.AddInMemoryMetrics();
        }

        services.AddAgents(configuration);
        services.AddInProcessPipelineClient();

        // Cost governance: the budget gate (run-level enforcement) + the tenant-explicit budget service
        // the Cost app uses to read/set the cap. Scoped — depends on the scoped run repository.
        services.AddScoped<AgentOs.Domain.Cost.IBudgetGuard, Cost.BudgetGuard>();
        services.AddScoped<Cost.IBudgetService, Cost.BudgetService>();

        // Runtime per-tenant prompt overrides: the resolver the agents consult (degrades to the default
        // prompt when no config store is wired) + the tenant-explicit read/write the Prompts app uses.
        services.AddSingleton<Prompts.IPromptOverrides>(sp => new Prompts.AppConfigPromptOverrides(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Prompts.AppConfigPromptOverrides>>(),
            sp.GetService<AgentOs.Modules.AppConfig.IAppConfigStore>()));
        services.AddScoped<Prompts.IPromptOverrideService, Prompts.PromptOverrideService>();

        // Persistence: real EF + Postgres when a connection string is set; otherwise no-op repos so
        // the host still boots stateless (Persistence:RequireDatabase=false opts into the legacy path).
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<PipelineDbContext>(opt =>
                opt.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "pipeline"))
                   .AddInterceptors(AgentOs.SharedKernel.Persistence.RowLevelSecurityInterceptor.Instance));
            services.AddNpgsqlConnectionFactory(connectionString);
            services.AddScoped<IPipelineRunRepository, PipelineRunRepository>();
            services.AddScoped<IOrchestrationRepository, OrchestrationRepository>();
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
            services.AddSingleton<IPipelineRunRepository, NullPipelineRunRepository>();
            services.AddSingleton<IOrchestrationRepository, NullOrchestrationRepository>();
        }

        services.TryAddSingleton<IAuthTokenProvider>(NullAuthTokenProvider.Instance);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPipelineEndpoints();
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetService<PipelineDbContext>();
        if (db is not null)
        {
            // Advisory lock: serialise concurrent replicas racing the same migration at boot.
            await using var migrationLock = await AgentOs.SharedKernel.Persistence.PgAdvisoryLock
                .AcquireAsync(db.Database.GetConnectionString(), "agentos:migrate:pipeline", ct).ConfigureAwait(false);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
    }
}
