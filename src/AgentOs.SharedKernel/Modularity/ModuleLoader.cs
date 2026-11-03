// Reflection-based module discovery + invocation. The host calls AddModulesFromAssemblies once at
// startup with the set of assemblies that ship modules; ModuleLoader instantiates every concrete
// IModule, invokes AddServices, and registers the instance in DI so MapModuleEndpoints +
// InitializeModulesAsync can later resolve them back without re-instantiating.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.SharedKernel.Modularity;

/// <summary>Host-side helpers to discover and drive <see cref="IModule"/> instances.</summary>
public static class ModuleLoader
{
    /// <summary>Scan the given assemblies for concrete <see cref="IModule"/> implementations,
    /// instantiate each via its parameterless constructor, register the instance in DI as a singleton,
    /// and call <see cref="IModule.AddServices"/>.</summary>
    public static IServiceCollection AddModulesFromAssemblies(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(assemblies);

        // Honest degraded mode: surfaces whether the no-op-repository boot is active so UIs can
        // say "requires a database" instead of fake-succeeding into a no-op repo.
        services.AddSingleton(new Persistence.DatabaseAvailability(
            !string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection"))));

        foreach (var module in DiscoverModules(assemblies))
        {
            services.AddSingleton(module);
            module.AddServices(services, configuration);
        }
        return services;
    }

    /// <summary>Resolve every registered <see cref="IEndpointModule"/> and call MapEndpoints.</summary>
    public static IEndpointRouteBuilder MapModuleEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (var module in app.Services.GetServices<IModule>().OfType<IEndpointModule>())
        {
            module.MapEndpoints(app);
        }
        return app;
    }

    /// <summary>
    /// Map every module's endpoints behind the versioned REST contract (E1.3): REST modules under <c>/v1</c> plus their
    /// deprecated unversioned twins (<see cref="Http.ApiVersioning.MapVersioned"/>); <see cref="IProtocolEndpointModule"/>s
    /// once at their own path. <paramref name="hostRoutes"/> adds the host's own REST routes to the same contract.
    /// </summary>
    public static IEndpointRouteBuilder MapVersionedModuleEndpoints(this WebApplication app, Action<IEndpointRouteBuilder>? hostRoutes = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var modules = app.Services.GetServices<IModule>().OfType<IEndpointModule>().ToArray();
        foreach (var protocol in modules.OfType<IProtocolEndpointModule>())
        {
            protocol.MapEndpoints(app);
        }
        Http.ApiVersioning.MapVersioned(app, group =>
        {
            foreach (var module in modules.Where(m => m is not IProtocolEndpointModule))
            {
                module.MapEndpoints(group);
            }
            hostRoutes?.Invoke(group);
        });
        return app;
    }

    /// <summary>Resolve every registered <see cref="IInitializableModule"/> and await InitializeAsync
    /// sequentially.</summary>
    public static async Task InitializeModulesAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var module in services.GetServices<IModule>().OfType<IInitializableModule>())
        {
            await module.InitializeAsync(services, ct).ConfigureAwait(false);
        }
    }

    private static IEnumerable<IModule> DiscoverModules(Assembly[] assemblies)
    {
        foreach (var asm in assemblies.Distinct())
        {
            foreach (var type in asm.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }
                if (!typeof(IModule).IsAssignableFrom(type))
                {
                    continue;
                }
                var ctor = type.GetConstructor(Type.EmptyTypes)
                    ?? throw new InvalidOperationException(
                        $"Module {type.FullName} must declare a public parameterless constructor.");
                yield return (IModule)ctor.Invoke(null);
            }
        }
    }
}
