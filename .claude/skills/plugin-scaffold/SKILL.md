---
name: plugin-scaffold
description: >
  Scaffold a new AgentOS.NET plugin (IAgentOsPlugin) — a runtime-discovered extension dropped in the
  host's plugins/ folder, with no compile-time reference. Generates the project (SharedKernel + Domain
  references, Private=false), the plugin class + manifest, and a contributed capability (a tool, an LLM
  provider, and/or a desktop window). Use when the user says "scaffold a plugin", "new plugin X",
  "add a plugin", or invokes "/plugin-scaffold X".
---

Scaffold one AgentOS.NET plugin end-to-end. The template is the in-repo reference
`samples/AgentOs.Plugins.Sample` (the `IAgentOsPlugin` contract lives in `AgentOs.SharedKernel.Plugins`).

## Input

1. **Name** (PascalCase). Ex: `Jira`, `Slack`, `Linkup`.
2. **Id**: stable reverse-dns string, e.g. `acme.jira.tools` (used for de-dup + display).
3. **Contributes** (one or more): `tool` (an `ITool`), `provider` (a keyed `ILlmClient`), `window` (a Blazor desktop window — render-only today, see Notes).
4. **Location**: a folder NOT referenced by any host. Default `samples/AgentOs.Plugins.{Name}` (add to the slnx so CI compiles it).

## Output

### 1. `…/AgentOs.Plugins.{Name}.csproj`

Plain class library — OR `Microsoft.NET.Sdk.Razor` + `<FrameworkReference Include="Microsoft.AspNetCore.App" />` when it contributes a **window**.

```xml
<Project Sdk="Microsoft.NET.Sdk">          <!-- Microsoft.NET.Sdk.Razor if it ships a window -->
  <PropertyGroup>
    <RootNamespace>AgentOs.Plugins.{Name}</RootNamespace>
    <AssemblyName>AgentOs.Plugins.{Name}</AssemblyName>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <!-- Private=false: the host already loads these; the plugin binds to the host's copies at runtime. -->
    <ProjectReference Include="..\..\src\AgentOs.SharedKernel\AgentOs.SharedKernel.csproj" Private="false" />
    <ProjectReference Include="..\..\src\AgentOs.Domain\AgentOs.Domain.csproj" Private="false" />
  </ItemGroup>
</Project>
```

### 2. `…/​{Name}Plugin.cs`

```csharp
using AgentOs.Domain.Tools;            // if it contributes a tool
using AgentOs.SharedKernel.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Plugins.{Name};

public sealed class {Name}Plugin : IAgentOsPlugin   // MUST have a public parameterless ctor
{
    public PluginManifest Manifest { get; } = new(
        Id: "{id}", Name: "{Name}", Version: "1.0.0", Author: "{author}",
        Description: "{one line}");

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITool, {Name}Tool>();                 // contributes: tool
        // services.AddKeyedSingleton<ILlmClient>("{Name}", ...);   // contributes: provider (resolved by its own key)
        // services.AddSingleton(new PluginAppDescriptor(           // contributes: window
        //     "{id}.window", "{Name}", "squares-stack", "…", typeof({Name}App)));
    }
}
```

### 3. The contributed capability

- **tool** → `{Name}Tool.cs : ITool` (Definition + `InvokeAsync`); mirror `samples/AgentOs.Plugins.Sample/WordCountTool.cs`. Tools flow through `IToolGateway` (policy + evidence) like first-party tools — no extra wiring.
- **provider** → an `ILlmClient` registered keyed under `"{Name}"`; the gateway's `NormalizeKey` falls through to the key, so users select it by that exact name.
- **window** → `{Name}App.razor` using **only global shell CSS** (`page-head`/`.metrics`/`.data-table` — no host-component references); registered via `PluginAppDescriptor`. Mirror `samples/AgentOs.Plugins.Sample/WordCountApp.razor`.

### 4. Build + load

`scripts/build-plugins.ps1` builds the sample and drops its DLL into `src/AgentOs.{Api,Web}/plugins/`.
Extend it (or copy the pattern) to also build the new plugin, then restart the host — the loader
(`AgentOs.SharedKernel.Plugins.PluginLoader`) discovers it and the **Plugins** desktop app lists it.

## Notes / rules

- The plugin class needs a **public parameterless constructor** (instantiated reflectively before the DI container exists).
- Reference **SharedKernel + Domain only** — never a host project; only ONE plugin DLL is copied (deps unify with the host's loaded copies).
- **Windows are render-only today**: a component from a runtime-discovered assembly renders but its interactive event handlers aren't wired to the Blazor Server circuit. Keep plugin windows read-only (dashboards, info, rendered data) until interactive-plugin-UI lands.
- Verify: build the plugin → `build-plugins.ps1` → run `src/AgentOs.Web` → open **Plugins** (it shows the plugin + its contributed capabilities) and, for a window, open the app.
