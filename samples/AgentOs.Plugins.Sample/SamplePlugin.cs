// A reference plugin. Demonstrates the whole contract: it is discovered at runtime from the host's
// plugins/ folder (no compile-time reference), advertises a manifest the desktop shows, and contributes
// a tool through the standard DI surface — the same ITool seam first-party modules use.

using AgentOs.Domain.Tools;
using AgentOs.SharedKernel.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Plugins.Sample;

/// <summary>Sample AgentOS.NET plugin — contributes the <see cref="WordCountTool"/>.</summary>
public sealed class SamplePlugin : IAgentOsPlugin
{
    /// <inheritdoc />
    public PluginManifest Manifest { get; } = new(
        Id: "agentos.sample.tools",
        Name: "Sample Tools",
        Version: "1.1.0",
        Author: "AgentOS.NET",
        Description: "Reference plugin — contributes a word_count tool and a desktop window, discovered at runtime.");

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITool, WordCountTool>();
        services.AddSingleton(new PluginAppDescriptor(
            Key: "sample.window",
            Title: "Sample Plugin",
            Icon: "squares-stack",
            Caption: "A plugin-contributed desktop window",
            ComponentType: typeof(WordCountApp)));
    }
}
