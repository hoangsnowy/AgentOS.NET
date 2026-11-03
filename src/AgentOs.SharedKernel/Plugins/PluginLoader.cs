// Runtime plugin discovery. Loads plugin assemblies from a folder (or takes assemblies directly, for
// tests), finds every IAgentOsPlugin, registers it through the SAME machinery as a first-party module
// (AddSingleton<IModule> + AddServices), and records the outcome in an IPluginCatalog the desktop reads.
// A bad plugin is captured as a Failed entry — it never crashes host startup.
//
// GAP-19 — a plugin runs with full trust in the host process, so only assemblies an operator approved are loaded: every
// *.dll's SHA-256 must be listed in Plugins:AllowedSha256 (bootstrap config — loading code is a deployment decision, not
// a tenant setting). Anything else is refused BEFORE any of its code is loaded: recorded as Refused with its hash (shown
// on Plugins in the desktop, and logged at startup by LogOutcomes) so the operator can approve it on purpose. The bytes
// that were hashed are the bytes that are loaded (LoadFromStream), so the file can't be swapped between check and load.
// An empty allowlist loads nothing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Tools;
using AgentOs.SharedKernel.Modularity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.SharedKernel.Plugins;

/// <summary>Host-side helpers to discover and wire <see cref="IAgentOsPlugin"/> instances at startup.</summary>
public static partial class PluginLoader
{
    /// <summary>Bootstrap key listing the SHA-256 (hex) of every plugin assembly the host may load.</summary>
    public const string AllowedSha256Key = "Plugins:AllowedSha256";

    /// <summary>The approved plugin hashes (lower-case hex). Throws on an entry that is not a 64-digit hex SHA-256.</summary>
    public static FrozenSet<string> ReadAllowlist(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in AgentOs.SharedKernel.Hosting.ConfigurationLists.Read(configuration, AllowedSha256Key))
        {
            var hex = entry.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? entry[7..] : entry;
            if (hex.Length != 64 || !hex.All(char.IsAsciiHexDigit))
            {
                throw new InvalidOperationException($"{AllowedSha256Key}: '{entry}' is not a SHA-256 hash (64 hex digits).");
            }
            hashes.Add(hex.ToLowerInvariant());
        }
        return hashes.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>The lower-case hex SHA-256 of <paramref name="bytes"/> — the form <see cref="AllowedSha256Key"/> lists.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Log what the loader found: a warning per refused plugin (with the hash to approve), an error per failed
    /// one, information per loaded one. Call once at startup.</summary>
    public static void LogOutcomes(IPluginCatalog catalog, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var plugin in catalog.Plugins)
        {
            switch (plugin.Status)
            {
                case PluginStatus.Refused:
                    LogRefused(logger, plugin.AssemblyPath, plugin.Sha256 ?? "?", AllowedSha256Key);
                    break;
                case PluginStatus.Failed:
                    LogFailed(logger, plugin.Manifest.Id, plugin.AssemblyPath, plugin.Error ?? "?");
                    break;
                default:
                    LogLoaded(logger, plugin.Manifest.Id, plugin.Manifest.Version, plugin.Sha256 ?? "in-memory");
                    break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Plugin {Path} refused: its SHA-256 {Sha256} is not in {Key}. Nothing from it was loaded. Add the hash to approve it.")]
    private static partial void LogRefused(ILogger logger, string path, string sha256, string key);

    [LoggerMessage(Level = LogLevel.Error, Message = "Plugin {Id} ({Path}) failed to load: {Error}")]
    private static partial void LogFailed(ILogger logger, string id, string path, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {Id} {Version} loaded (SHA-256 {Sha256}).")]
    private static partial void LogLoaded(ILogger logger, string id, string version, string sha256);

    /// <summary>Load every <c>*.dll</c> in <paramref name="pluginsPath"/>, register the plugins each
    /// contains, and register the resulting <see cref="IPluginCatalog"/>. A missing folder is a no-op
    /// (empty catalog). Call once, after the host's compile-time modules are added.</summary>
    public static IServiceCollection AddPlugins(
        this IServiceCollection services,
        IConfiguration configuration,
        string pluginsPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var results = new List<LoadedPlugin>();
        var assemblies = new List<Assembly>();
        var sources = new Dictionary<Assembly, (string Path, string Sha256)>();
        var allowed = ReadAllowlist(configuration);

        if (!string.IsNullOrWhiteSpace(pluginsPath) && Directory.Exists(pluginsPath))
        {
            foreach (var dll in Directory.GetFiles(pluginsPath, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    var full = Path.GetFullPath(dll);
                    var bytes = File.ReadAllBytes(full);
                    var sha256 = Sha256(bytes);
                    if (!allowed.Contains(sha256))
                    {
                        results.Add(Refused(full, sha256));
                        continue;
                    }
                    using var image = new MemoryStream(bytes, writable: false);
                    var assembly = AssemblyLoadContext.Default.LoadFromStream(image);
                    assemblies.Add(assembly);
                    sources[assembly] = (full, sha256);
                }
                catch (UnauthorizedAccessException ex) { results.Add(FailedFile(dll, ex)); }
                // A broken plugin file must be reported, not crash host startup.
                // FileLoadException + FileNotFoundException derive from IOException → before it (CS0160).
                catch (BadImageFormatException ex) { results.Add(FailedFile(dll, ex)); }
                catch (FileLoadException ex) { results.Add(FailedFile(dll, ex)); }
                catch (FileNotFoundException ex) { results.Add(FailedFile(dll, ex)); }
                catch (ReflectionTypeLoadException ex) { results.Add(FailedFile(dll, ex)); }
                catch (InvalidOperationException ex) { results.Add(FailedFile(dll, ex)); }
                catch (IOException ex) { results.Add(FailedFile(dll, ex)); }
            }
        }

        RegisterPlugins(services, configuration, assemblies, results, sources);
        services.AddSingleton<IPluginCatalog>(new PluginCatalog(results));
        return services;
    }

    /// <summary>Register plugins from already-loaded assemblies (the testable core). Registers the
    /// resulting <see cref="IPluginCatalog"/>.</summary>
    public static IServiceCollection AddPluginsFromAssemblies(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(assemblies);

        var results = new List<LoadedPlugin>();
        RegisterPlugins(services, configuration, assemblies, results, []);
        services.AddSingleton<IPluginCatalog>(new PluginCatalog(results));
        return services;
    }

    private static void RegisterPlugins(
        IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<Assembly> assemblies,
        List<LoadedPlugin> sink,
        Dictionary<Assembly, (string Path, string Sha256)> sources)
    {
        foreach (var assembly in assemblies)
        {
            // A plugin loaded from bytes has no Location — report the file it came from (and its approved hash).
            var location = assembly.Location;
            string? sha256 = null;
            if (sources.TryGetValue(assembly, out var source))
            {
                (location, sha256) = source;
            }
            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || !typeof(IAgentOsPlugin).IsAssignableFrom(type))
                {
                    continue;
                }

                // Construct + read the manifest first, so a later AddServices failure still keeps the
                // plugin's real identity in the catalog (not a synthetic type-name placeholder).
                IAgentOsPlugin plugin;
                PluginManifest manifest;

                // A plugin that won't even construct must not abort the others.
                void RecordConstructFailure(Exception e) =>
                    sink.Add(new LoadedPlugin(
                        new PluginManifest(type.FullName ?? type.Name, type.Name, "?"),
                        PluginStatus.Failed, location, [], e.Message, sha256));

                try
                {
                    plugin = (IAgentOsPlugin)Activator.CreateInstance(type)!;
                    manifest = plugin.Manifest;
                }
                // MissingMethodException → MissingMemberException → MemberAccessException
                // (derived→base) — derived clauses precede their bases (CS0160).
                catch (TargetInvocationException ex) { RecordConstructFailure(ex); continue; }
                catch (MissingMethodException ex) { RecordConstructFailure(ex); continue; }
                catch (MissingMemberException ex) { RecordConstructFailure(ex); continue; }
                catch (MemberAccessException ex) { RecordConstructFailure(ex); continue; }
                catch (TypeLoadException ex) { RecordConstructFailure(ex); continue; }
                catch (InvalidOperationException ex) { RecordConstructFailure(ex); continue; }
                catch (InvalidCastException ex) { RecordConstructFailure(ex); continue; }

                try
                {
                    // Run the plugin's own registrations first; only on success register it as a module
                    // (so MapModuleEndpoints / InitializeModulesAsync see it) and record the catalog entry.
                    var startIndex = services.Count;
                    plugin.AddServices(services, configuration);
                    var capabilities = SummarizeCapabilities(services, startIndex);
                    services.AddSingleton<IModule>(plugin);

                    sink.Add(new LoadedPlugin(manifest, PluginStatus.Loaded, location, capabilities, Sha256: sha256));
                }
                // One plugin throwing must not abort discovery of the others.
                // MissingMethodException → MissingMemberException → MemberAccessException
                // (derived→base) — derived clauses precede their bases (CS0160).
                catch (TargetInvocationException ex) { RecordRegisterFailure(ex); }
                catch (TypeLoadException ex) { RecordRegisterFailure(ex); }
                catch (MissingMethodException ex) { RecordRegisterFailure(ex); }
                catch (MissingMemberException ex) { RecordRegisterFailure(ex); }
                catch (MemberAccessException ex) { RecordRegisterFailure(ex); }
                catch (FileLoadException ex) { RecordRegisterFailure(ex); }
                catch (FileNotFoundException ex) { RecordRegisterFailure(ex); }
                catch (BadImageFormatException ex) { RecordRegisterFailure(ex); }
                catch (InvalidOperationException ex) { RecordRegisterFailure(ex); }
                catch (NotSupportedException ex) { RecordRegisterFailure(ex); }
                catch (ArgumentException ex) { RecordRegisterFailure(ex); }

                void RecordRegisterFailure(Exception e) =>
                    sink.Add(new LoadedPlugin(manifest, PluginStatus.Failed, location, [], e.Message, sha256));
            }
        }
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
    }

    private static List<string> SummarizeCapabilities(IServiceCollection services, int startIndex)
    {
        var tools = 0;
        var apps = 0;
        var others = 0;
        var providers = new List<string>();

        for (var i = startIndex; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(ITool))
            {
                tools++;
            }
            else if (descriptor.ServiceType == typeof(PluginAppDescriptor))
            {
                apps++;
            }
            else if (descriptor.ServiceType == typeof(ILlmClient) && descriptor.IsKeyedService)
            {
                providers.Add(descriptor.ServiceKey?.ToString() ?? "?");
            }
            else
            {
                others++;
            }
        }

        var caps = new List<string>();
        if (tools > 0)
        {
            caps.Add(tools == 1 ? "1 tool" : $"{tools} tools");
        }
        if (apps > 0)
        {
            caps.Add(apps == 1 ? "1 app" : $"{apps} apps");
        }
        caps.AddRange(providers.Select(p => $"provider: {p}"));
        if (others > 0)
        {
            caps.Add(others == 1 ? "1 service" : $"{others} services");
        }
        return caps;
    }

    private static LoadedPlugin Refused(string dllPath, string sha256)
    {
        var name = Path.GetFileNameWithoutExtension(dllPath);
        return new LoadedPlugin(
            new PluginManifest(name, name, "?"), PluginStatus.Refused, dllPath, [],
            $"Not approved: this file's SHA-256 is not in {AllowedSha256Key}, so none of its code was loaded. "
            + $"If you trust it, an operator adds sha256 {sha256} to {AllowedSha256Key} and restarts the host.",
            sha256);
    }

    private static LoadedPlugin FailedFile(string dllPath, Exception ex)
    {
        var name = Path.GetFileNameWithoutExtension(dllPath);
        return new LoadedPlugin(
            new PluginManifest(name, name, "?"), PluginStatus.Failed, Path.GetFullPath(dllPath), [], ex.Message);
    }
}
