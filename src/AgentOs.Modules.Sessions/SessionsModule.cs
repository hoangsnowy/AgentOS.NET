// M3 — Sessions module entry. Registers the runner + session repositories, the runner directory used by
// the RemoteAgent hub's pairing handshake, the (always-on) pairing service, the DbContext, mounts the
// /sessions + /runners endpoints, and applies the EF migration at startup. When no DB is configured the
// repos + directory fall back to no-ops so the host still boots (same convention as WorkspacesModule).
//
// The pairing service is registered unconditionally (singleton, stateless crypto) because both the
// endpoints (issue) and the RemoteAgent hub (verify) resolve it from this one container.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.Sessions.Endpoints;
using AgentOs.Modules.Sessions.Pairing;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.SharedKernel.Modularity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentOs.Modules.Sessions;

public sealed class SessionsModule : IModule, IEndpointModule, IInitializableModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRunnerPairingService, RunnerPairingService>();

        // Runner provisioning (shared by the /runners endpoint + the VS Code browser-pairing flow) and
        // the one-time exchange-code store for that flow. Stateless / in-memory, so always registered.
        services.TryAddScoped<IRunnerProvisioningService, RunnerProvisioningService>();
        // One store behind both seams: the editor flow (IPairingCodeStore) and the typed code a member reads off
        // Board › Computers (IComputerPairingCodes) are redeemed by the same exchange endpoint.
        services.TryAddSingleton<PairingCodeStore>();
        services.TryAddSingleton<IPairingCodeStore>(sp => sp.GetRequiredService<PairingCodeStore>());
        services.TryAddSingleton<IComputerPairingCodes>(sp => sp.GetRequiredService<PairingCodeStore>());

        // M6 — live session-run event feed (singleton, in-memory): the issue-work agent + the run
        // worker publish progress to it and the desktop subscribes for live updates. Always registered
        // (stateless fan-out, no DB) so streaming works in both the full stack and the no-DB dev path.
        services.TryAddSingleton<ISessionRunFeed, InMemorySessionRunFeed>();

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<SessionsDbContext>(opt =>
                opt.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "sessions"))
                   .AddInterceptors(AgentOs.SharedKernel.Persistence.RowLevelSecurityInterceptor.Instance));
            services.AddNpgsqlConnectionFactory(connectionString);
            services.AddScoped<IRunnerRepository, RunnerRepository>();
            services.AddScoped<ISessionRepository, SessionRepository>();
            services.AddScoped<IRunnerDirectory, EfRunnerDirectory>();
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
            services.AddSingleton<IRunnerRepository, NullRunnerRepository>();
            services.AddSingleton<ISessionRepository, NullSessionRepository>();
            services.AddSingleton<IRunnerDirectory, NullRunnerDirectory>();
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapSessionEndpoints();
        // NOTE: the VS Code browser-pairing endpoints (PairingEndpoints) are mapped on the Web host, not
        // here — they rely on the browser's OIDC cookie session, which only the Web has.
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetService<SessionsDbContext>();
        if (db is not null)
        {
            // Advisory lock: serialise concurrent replicas racing the same migration at boot.
            await using var migrationLock = await AgentOs.SharedKernel.Persistence.PgAdvisoryLock
                .AcquireAsync(db.Database.GetConnectionString(), "agentos:migrate:sessions", ct).ConfigureAwait(false);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
    }
}
