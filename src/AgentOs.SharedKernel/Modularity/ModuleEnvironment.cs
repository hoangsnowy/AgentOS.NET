// Modules receive only IConfiguration in AddServices (never IHostEnvironment — see IModule), so a
// module that needs a Development-vs-Production default reads the environment name straight out of
// configuration. ASP.NET binds ASPNETCORE_ENVIRONMENT (and the generic-host DOTNET_ENVIRONMENT) into
// configuration, so both keys are present at AddServices time. Used for fail-closed-in-Production
// defaults (tool policy enforcement, build-verifier gating).

using System;
using Microsoft.Extensions.Configuration;

namespace AgentOs.SharedKernel.Modularity;

/// <summary>Reads the host environment name from configuration for modules that only see IConfiguration.</summary>
public static class ModuleEnvironment
{
    /// <summary>The resolved environment name (ASPNETCORE_ENVIRONMENT, else DOTNET_ENVIRONMENT, else empty).</summary>
    public static string Name(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration["ASPNETCORE_ENVIRONMENT"]
            ?? configuration["DOTNET_ENVIRONMENT"]
            ?? string.Empty;
    }

    /// <summary><c>true</c> when the host environment is Production.</summary>
    public static bool IsProduction(IConfiguration configuration)
        => string.Equals(Name(configuration), "Production", StringComparison.OrdinalIgnoreCase);

    public static bool IsDevelopment(IConfiguration configuration)
        => string.Equals(Name(configuration), "Development", StringComparison.OrdinalIgnoreCase);
}
