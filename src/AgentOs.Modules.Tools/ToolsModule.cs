// Epic E1 — Tools module entry. Registers IToolRegistry as a singleton (registry state must
// outlive a single request scope so MCP probes and the orchestrator share one view). Tools
// themselves are discovered via ITool DI registrations and pumped into the registry on startup.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;
using AgentOs.Modules.Tools.Evidence;
using AgentOs.Modules.Tools.Persistence;
using AgentOs.Modules.Tools.Policy;
using AgentOs.Modules.Tools.Registry;
using AgentOs.SharedKernel.Modularity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Tools;

public sealed class ToolsModule : IModule, IInitializableModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IToolRegistry, InMemoryToolRegistry>();
        // Per-tenant tool allowlist read from AppConfig. Default-permissive in Development until an admin
        // turns on enforcement in the Policy app, but FAIL-CLOSED BY DEFAULT IN PRODUCTION (deny unless a
        // tenant has an explicit allowlist) so an untrusted tenant cannot invoke an un-vetted tool out of
        // the box. `Tools:EnforceByDefault` overrides the env-derived default either way. IAppConfigStore is
        // optional, so this still degrades to permissive when no config store is wired (no-DB standalone),
        // regardless of the default — there is nothing to enforce against.
        var enforceByDefault = configuration.GetValue("Tools:EnforceByDefault", ModuleEnvironment.IsProduction(configuration));
        services.AddSingleton<AgentOs.Domain.Configuration.ISettingsContributor>(new ToolsSettings(enforceByDefault));
        services.TryAddSingleton<IToolPolicy>(sp => new Policy.AppConfigToolPolicy(
            sp.GetService<AgentOs.Modules.AppConfig.IAppConfigStore>(), enforceByDefault,
            sp.GetService<AgentOs.Domain.Configuration.ISettings>()));
        services.TryAddSingleton<IToolPolicyService, Policy.ToolPolicyService>();

        // Evidence sink: durable EF-backed when a DB is configured, else the in-memory ring buffer
        // (dev / CI). The schema `tools` keeps tool-invocation evidence as a first-class audit trail.
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        // The inner sink is the real persistence (durable EF when a DB is wired, else the in-memory ring).
        // It is wrapped by BufferedToolInvocationLog so the gateway's AppendAsync is a non-blocking enqueue
        // (M6 perf — keeps per-tool-call DB writes off the run's critical path; drained on a background loop
        // started in InitializeAsync).
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<ToolsDbContext>(opt =>
                opt.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "tools"))
                   .AddInterceptors(AgentOs.SharedKernel.Persistence.RowLevelSecurityInterceptor.Instance));
            services.AddNpgsqlConnectionFactory(connectionString);
            services.TryAddSingleton<EfToolInvocationLog>();
            services.TryAddSingleton<IToolInvocationLog>(sp =>
                new BufferedToolInvocationLog(
                    sp.GetRequiredService<EfToolInvocationLog>(),
                    sp.GetService<ILogger<BufferedToolInvocationLog>>()));
        }
        else
        {
            services.TryAddSingleton<InMemoryToolInvocationLog>();
            services.TryAddSingleton<IToolInvocationLog>(sp =>
                new BufferedToolInvocationLog(
                    sp.GetRequiredService<InMemoryToolInvocationLog>(),
                    sp.GetService<ILogger<BufferedToolInvocationLog>>()));
        }

        // M1 — the shared policy-gate + invoke + evidence seam. Every governed execution path
        // (the in-process LLM tool loop and the remote-session executor) resolves this so tools
        // are gated + recorded identically regardless of where the side effect runs.
        services.TryAddSingleton<IToolGateway>(sp => new DefaultToolGateway(
            sp.GetService<IToolPolicy>(),
            sp.GetService<IToolInvocationLog>(),
            sp.GetService<AgentOs.Domain.Configuration.ISettings>()));
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        var registry = services.GetRequiredService<IToolRegistry>();
        foreach (var tool in services.GetServices<ITool>())
        {
            registry.Register(tool);
        }

        // Start the single background drain loop for the buffered evidence log.
        (services.GetService<IToolInvocationLog>() as BufferedToolInvocationLog)?.StartDraining();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetService<ToolsDbContext>();
        if (db is not null)
        {
            // Advisory lock: serialise concurrent replicas racing the same migration at boot.
            await using var migrationLock = await AgentOs.SharedKernel.Persistence.PgAdvisoryLock
                .AcquireAsync(db.Database.GetConnectionString(), "agentos:migrate:tools", ct).ConfigureAwait(false);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
    }
}
