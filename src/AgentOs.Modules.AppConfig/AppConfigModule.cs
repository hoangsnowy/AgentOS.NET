// Module entry: registers IAppConfigStore + AppConfigDbContext, mounts /settings endpoints, and
// applies the EF migration at startup. When no DB connection string is configured the store falls
// back to InMemoryAppConfigStore so the host still boots stateless.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.AppConfig.Endpoints;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.Modules.AppConfig.Persistence;
using AgentOs.SharedKernel.Modularity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentOs.Modules.AppConfig;

public sealed class AppConfigModule : IModule, IEndpointModule, IInitializableModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // UI-first settings registry (ADR-0011): the catalog collects every module's ISettingsContributor once;
        // ISettings resolves lock → tenant → platform → seed → default over the store registered below.
        services.TryAddSingleton<ISettingsCatalog, SettingsCatalog>();
        services.TryAddSingleton<ISettingsAuditSink, NullSettingsAuditSink>();
        // The seed/lock source is the host configuration; fall back to the one handed to AddServices so hosts
        // (and tests) that don't register IConfiguration in DI still resolve the registry.
        services.TryAddSingleton<ISettings>(sp => new SettingsService(
            sp.GetRequiredService<ISettingsCatalog>(),
            sp.GetRequiredService<IAppConfigStore>(),
            sp.GetService<IConfiguration>() ?? configuration,
            sp.GetRequiredService<ISettingsAuditSink>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SettingsService>>(),
            sp.GetService<TimeProvider>()));

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<AppConfigDbContext>(opt =>
                opt.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "config")));
            services.AddSingleton<IAppConfigStore, EfAppConfigStore>();
            return;
        }

        services.TryAddSingleton<IAppConfigStore, InMemoryAppConfigStore>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapSettingsEndpoints();
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Build the settings catalog now so a duplicate key or an invalid default aborts startup with a named
        // error instead of surfacing on the first Settings read.
        _ = services.GetService<ISettingsCatalog>();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetService<AppConfigDbContext>();
        if (db is not null)
        {
            // Advisory lock: serialise concurrent replicas racing the same migration at boot.
            await using var migrationLock = await AgentOs.SharedKernel.Persistence.PgAdvisoryLock
                .AcquireAsync(db.Database.GetConnectionString(), "agentos:migrate:config", ct).ConfigureAwait(false);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);

            // The shared DataProtection key ring (registered by AddAgentOsDataProtection on the host) lives in
            // the same `config` schema; migrate it here so the keys table exists before the first protect call.
            var dpDb = scope.ServiceProvider.GetService<DataProtectionDbContext>();
            if (dpDb is not null)
            {
                await dpDb.Database.MigrateAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            // No relational AppConfig context (stateless boot) — the key-ring context may still exist.
            var dpDb = scope.ServiceProvider.GetService<DataProtectionDbContext>();
            if (dpDb is not null)
            {
                await using var migrationLock = await AgentOs.SharedKernel.Persistence.PgAdvisoryLock
                    .AcquireAsync(dpDb.Database.GetConnectionString(), "agentos:migrate:config", ct).ConfigureAwait(false);
                await dpDb.Database.MigrateAsync(ct).ConfigureAwait(false);
            }
        }
    }
}
