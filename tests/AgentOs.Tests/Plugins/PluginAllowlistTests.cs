// GAP-19 — plugins load only when their SHA-256 is in Plugins:AllowedSha256. A real plugin assembly is compiled with
// Roslyn into a temp plugins folder: approved → Loaded (its tool registered); not approved → Refused with its hash, and
// none of its code is loaded (the assembly never appears in the AppDomain).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentOs.SharedKernel.Plugins;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Plugins;

public sealed class PluginAllowlistTests : IDisposable
{
    private readonly string _folder = Path.Join(Path.GetTempPath(), "agentos-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginAllowlistTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static IConfiguration Config(params string[] hashes) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [PluginLoader.AllowedSha256Key] = string.Join(';', hashes) })
        .Build();

    private static IPluginCatalog Load(IServiceCollection services, IConfiguration config, string folder)
    {
        services.AddPlugins(config, folder);
        return services.BuildServiceProvider().GetRequiredService<IPluginCatalog>();
    }

    [Fact]
    public void ApprovedHash_LoadsThePlugin()
    {
        var (path, name) = CompilePlugin();
        var hash = PluginLoader.Sha256(File.ReadAllBytes(path));
        var services = new ServiceCollection();

        var plugin = Load(services, Config(hash.ToUpperInvariant()), _folder).Plugins.ShouldHaveSingleItem();

        plugin.Status.ShouldBe(PluginStatus.Loaded);
        plugin.Manifest.Id.ShouldBe(name);
        plugin.Sha256.ShouldBe(hash);
        plugin.AssemblyPath.ShouldBe(Path.GetFullPath(path));
        plugin.Capabilities.ShouldContain("1 tool");
    }

    [Fact]
    public void UnknownHash_IsRefused_AndNoneOfItsCodeIsLoaded()
    {
        var (path, name) = CompilePlugin();
        var other = new string('a', 64);

        var plugin = Load(new ServiceCollection(), Config(other), _folder).Plugins.ShouldHaveSingleItem();

        plugin.Status.ShouldBe(PluginStatus.Refused);
        plugin.Sha256.ShouldBe(PluginLoader.Sha256(File.ReadAllBytes(path)));
        plugin.Error!.ShouldContain(plugin.Sha256!);
        plugin.Error!.ShouldContain(PluginLoader.AllowedSha256Key);
        AppDomain.CurrentDomain.GetAssemblies().ShouldNotContain(a => a.GetName().Name == name);
    }

    [Fact]
    public void EmptyAllowlist_RefusesEverything()
    {
        CompilePlugin();
        File.WriteAllBytes(Path.Join(_folder, "garbage.dll"), [1, 2, 3]);

        var plugins = Load(new ServiceCollection(), new ConfigurationBuilder().Build(), _folder).Plugins;

        plugins.Count.ShouldBe(2);
        plugins.ShouldAllBe(p => p.Status == PluginStatus.Refused); // garbage is refused, not even parsed (no BadImageFormat)
    }

    [Fact]
    public void ApprovedButBroken_ReachesTheLoader_AndIsRecordedAsFailed()
    {
        var bytes = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(Path.Join(_folder, "broken.dll"), bytes);

        var plugin = Load(new ServiceCollection(), Config("sha256:" + PluginLoader.Sha256(bytes)), _folder).Plugins.ShouldHaveSingleItem();

        plugin.Status.ShouldBe(PluginStatus.Failed);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    public void InvalidAllowlistEntry_FailsTheBoot(string entry)
    {
        Should.Throw<InvalidOperationException>(() => PluginLoader.ReadAllowlist(Config(entry))).Message.ShouldContain(PluginLoader.AllowedSha256Key);
    }

    [Fact]
    public void Allowlist_ReadsArraysAndSeparators()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PluginLoader.AllowedSha256Key + ":0"] = a,
            [PluginLoader.AllowedSha256Key + ":1"] = $" {b} ",
        }).Build();

        PluginLoader.ReadAllowlist(config).ShouldBe([a, b.ToLowerInvariant()], ignoreOrder: true);
    }

    [Fact]
    public void LogOutcomes_WarnsWithTheHashToApprove()
    {
        var (path, _) = CompilePlugin();
        var catalog = Load(new ServiceCollection(), new ConfigurationBuilder().Build(), _folder);
        var logger = new FakeLogger();

        PluginLoader.LogOutcomes(catalog, logger);

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldContain(PluginLoader.Sha256(File.ReadAllBytes(path)));
        record.Message.ShouldContain(PluginLoader.AllowedSha256Key);
    }

    // A real IAgentOsPlugin (registers one ITool) compiled into its own uniquely named assembly in the plugins folder.
    private (string Path, string Name) CompilePlugin()
    {
        var name = "AgentOsTestPlugin_" + Guid.NewGuid().ToString("N");
        var source = $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using AgentOs.Domain.Tools;
            using AgentOs.SharedKernel.Plugins;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;

            public sealed class TestPlugin : IAgentOsPlugin
            {
                public PluginManifest Manifest { get; } = new("{{name}}", "Test plugin", "1.0.0");
                public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddSingleton<ITool, EchoTool>();
            }

            public sealed class EchoTool : ITool
            {
                public ToolDefinition Definition { get; } = new("echo", "echo", "{}");
                public Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken = default)
                    => Task.FromResult(ToolInvocationResult.Success("id", "ok"));
            }
            """;
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Join(_folder, name + ".dll");
        using (var file = File.Create(path))
        {
            var result = compilation.Emit(file);
            result.Success.ShouldBeTrue(string.Join(Environment.NewLine, result.Diagnostics));
        }
        return (path, name);
    }
}
