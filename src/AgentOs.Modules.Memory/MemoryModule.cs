// Knowledge graph module (ADR-0008, slice 1): the memory the three agentic phases share — tickets, proposed changes,
// and the lessons learned from reviews, failing checks and acceptance testing. Without a database it remembers
// nothing (NullMemoryStore) and the host still boots.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Memory;
using AgentOs.Modules.Memory.Persistence;
using AgentOs.SharedKernel.Modularity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentOs.Modules.Memory;

public sealed class MemoryModule : IModule, IInitializableModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<IMemoryStore, NullMemoryStore>();
            return;
        }
        services.AddDbContext<MemoryDbContext>(opt =>
            opt.UseNpgsql(connectionString, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "memory"))
               .AddInterceptors(AgentOs.SharedKernel.Persistence.RowLevelSecurityInterceptor.Instance));
        services.AddScoped<IMemoryStore, EfMemoryStore>();
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetService<MemoryDbContext>();
        if (db is null) { return; }
        await using var migrationLock = await AgentOs.SharedKernel.Persistence.PgAdvisoryLock
            .AcquireAsync(db.Database.GetConnectionString(), "agentos:migrate:memory", ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
    }
}
