// Module entry: registers IGitHubPrService + IBuildVerifier. Both consumed by Pipeline orchestrator
// when the run generates code that needs to land in a PR or be build-verified locally first.

using System;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration.Sources;
using AgentOs.Modules.Integration.Tools;
using AgentOs.Modules.Tools;
using AgentOs.SharedKernel.Modularity;
using AgentOs.SharedKernel.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.Integration;

public sealed class IntegrationModule : IModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddGitHubIntegration();
        // build_verifier runs LLM-generated source (MSBuild = arbitrary code execution) — off by default in
        // Production until a sandboxed runner is selected; Development defaults on. Config keys override.
        // ADR-0005: Sandbox=InProcess (Dev) or Container (Prod-safe, ephemeral no-egress `docker run`/ACA Job).
        var buildVerifierOptions = new BuildVerifierOptions(
            Enabled: configuration.GetValue("Integration:BuildVerifier:Enabled", !ModuleEnvironment.IsProduction(configuration)),
            Sandbox: configuration.GetValue("Integration:BuildVerifier:Sandbox", BuildSandboxMode.InProcess),
            ContainerImage: configuration.GetValue("Integration:BuildVerifier:ContainerImage", "mcr.microsoft.com/dotnet/sdk:10.0")!,
            CpuLimit: configuration.GetValue("Integration:BuildVerifier:CpuLimit", 2.0),
            MemoryLimit: configuration.GetValue("Integration:BuildVerifier:MemoryLimit", "1g")!,
            PidsLimit: configuration.GetValue("Integration:BuildVerifier:PidsLimit", 256),
            TimeoutSeconds: configuration.GetValue("Integration:BuildVerifier:TimeoutSeconds", 90));
        services.AddSingleton(buildVerifierOptions);
        if (buildVerifierOptions.Sandbox == BuildSandboxMode.Container)
        {
            services.AddSingleton<Sandbox.ISandboxedBuildRunner, Sandbox.ContainerBuildRunner>();
        }
        else
        {
            services.AddSingleton<Sandbox.ISandboxedBuildRunner, Sandbox.InProcessBuildRunner>();
        }
        // UI-first (ADR-0011): the startup values above are the defaults; Settings → Platform → Build sandbox
        // changes the gate, sandbox mode and quotas live. Each build gets a runner for the CURRENT mode.
        services.AddSingleton<AgentOs.Domain.Configuration.ISettingsContributor>(new BuildVerifierSettings(buildVerifierOptions));
        services.AddSingleton<IBuildVerifierOptionsSource>(sp =>
            sp.GetService<AgentOs.Domain.Configuration.ISettings>() is { } settings
                ? new SettingsBuildVerifierOptionsSource(settings, buildVerifierOptions)
                : new StaticBuildVerifierOptionsSource(buildVerifierOptions));
        services.AddTransient<IBuildVerifier>(sp => new BuildVerifier(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BuildVerifier>>(),
            sp.GetRequiredService<IBuildVerifierOptionsSource>(),
            opts => opts.Sandbox == BuildSandboxMode.Container
                ? new Sandbox.ContainerBuildRunner(opts)
                : new Sandbox.InProcessBuildRunner(opts)));
        services.AddTransient<IBoardTicketService, BoardTicketService>();
        services.AddTransient<IBoardWriteService, BoardWriteService>();
        services.AddTransient<AgentOs.Domain.Workspaces.IPrCreationService, GitHubPrCreationService>();
        // Epic E2 — wrap IBuildVerifier as an ITool so agents can call it via FunctionInvokingChatClient.
        // ToolsModule.InitializeAsync pumps every ITool DI registration into the IToolRegistry at startup.
        services.AddTool<BuildVerifierTool>();

        // M2 — source-control providers behind one seam (GitHub + Azure DevOps both live for repos;
        // ADO boards/work-items are a later milestone) + the by-kind resolver the Workspaces module consumes.
        services.AddHttpClient(); // IHttpClientFactory for the Azure DevOps REST provider
        // The ADO REST provider talks to a TENANT-SUPPLIED host → SSRF risk. Route its named client through
        // the SSRF-hardened handler that refuses to connect to private/loopback/link-local IPs (incl. via
        // DNS + redirects). The provider resolves this client by name (see AzureDevOpsSourceProvider.Client).
        services.AddHttpClient(nameof(AzureDevOpsSourceProvider))
            .ConfigurePrimaryHttpMessageHandler(SsrfGuard.CreateHardenedHandler);
        services.AddSingleton<ISourceProvider, GitHubSourceProvider>();
        services.AddSingleton<ISourceProvider, AzureDevOpsSourceProvider>();
        services.AddSingleton<ISourceProviderResolver, SourceProviderResolver>();
        services.AddSingleton<IRepoFileBrowser, GitHubRepoFileBrowser>();
    }
}
