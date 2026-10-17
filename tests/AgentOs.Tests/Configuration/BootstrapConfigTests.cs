// Phase C.6 — appsettings is bootstrap-only (ADR-0011). Code is the single source of product defaults, so the
// hosts can't drift (the old Web/Api files disagreed on provider casing), and the one environment-dependent
// default — offline LLM fallback — is derived from the environment instead of living in a host's appsettings.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Configuration;

public sealed class BootstrapConfigTests
{
    // What may appear in a base appsettings.json: what a host needs before anyone can sign in.
    private static readonly HashSet<string> BootstrapSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "//", "Logging", "AllowedHosts", "ConnectionStrings", "Persistence", "Api", "Auth", "Kestrel", "Settings",
    };

    [Theory]
    [InlineData("AgentOs.Web")]
    [InlineData("AgentOs.Api")]
    public void BaseAppSettings_ContainOnlyBootstrapSections(string host)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", host, "appsettings.json")));
        var product = doc.RootElement.EnumerateObject().Select(p => p.Name).Where(n => !BootstrapSections.Contains(n)).ToArray();
        product.ShouldBeEmpty($"{host}/appsettings.json declares product settings — give them a code default + a SettingDescriptor (UI) instead");
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData(null, false)]
    public void OfflineFallback_DefaultsOnOnlyInDevelopment(string? environment, bool expected)
    {
        var (options, catalog) = Build(new() { ["ASPNETCORE_ENVIRONMENT"] = environment });
        options.OfflineFallback.ShouldBe(expected);
        catalog.Single(d => d.Key == LlmSettings.OfflineFallback).DefaultValue.ShouldBe(expected ? "true" : "false");
    }

    [Fact]
    public void OfflineFallback_ExplicitValueStillWins_InDevelopment()
    {
        var (options, _) = Build(new() { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["Llm:OfflineFallback"] = "false" });
        options.OfflineFallback.ShouldBeFalse();
    }

    [Fact]
    public void LlmDefaults_ComeFromCode_WithNoConfiguration()
    {
        var (options, _) = Build([]);
        options.Provider.ShouldBe("Claude");
        options.Claude.Endpoint.ShouldBe("https://api.anthropic.com");
        options.AzureOpenAi.Model.ShouldBe("gpt-4.1");
    }

    private static (LlmOptions Options, SettingDescriptor[] Catalog) Build(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new LlmModule().AddServices(services, new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        using var sp = services.BuildServiceProvider();
        var catalog = sp.GetServices<ISettingsContributor>().OfType<LlmSettings>().Single().Describe().ToArray();
        return (sp.GetRequiredService<IOptions<LlmOptions>>().Value, catalog);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentOs.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("AgentOs.slnx not found above the test output directory.");
    }
}
