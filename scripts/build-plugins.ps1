#requires -Version 7
# Builds the sample plugin and drops its single assembly into each host's plugins/ folder, where the
# runtime plugin loader (AgentOs.SharedKernel.Plugins.PluginLoader) discovers it at startup. The output
# folders are gitignored. Run before launching a host if you want the sample plugin loaded; approve the SHA-256 it
# prints in Plugins:AllowedSha256 — unapproved plugins are refused (GAP-19).
#
#   pwsh scripts/build-plugins.ps1 [-Configuration Release]

param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'samples/AgentOs.Plugins.Sample/AgentOs.Plugins.Sample.csproj'

Write-Host "Building sample plugin ($Configuration)..."
dotnet build $proj -c $Configuration --nologo | Out-Host

$dll = Join-Path $root "samples/AgentOs.Plugins.Sample/bin/$Configuration/net10.0/AgentOs.Plugins.Sample.dll"
if (-not (Test-Path $dll)) { throw "Plugin DLL not found: $dll" }

foreach ($hostDir in @('src/AgentOs.Api/plugins', 'src/AgentOs.Web/plugins')) {
    $dest = Join-Path $root $hostDir
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item $dll $dest -Force
    Write-Host "Copied $(Split-Path $dll -Leaf) -> $hostDir"
}

# GAP-19: the hosts load only plugins whose SHA-256 is in Plugins:AllowedSha256 (docs/CONFIG.md). Print the value to
# approve this build with — it changes on every rebuild, so set it after building, then restart the host.
$sha256 = (Get-FileHash -Algorithm SHA256 $dll).Hash.ToLowerInvariant()
Write-Host ""
Write-Host "SHA-256 $sha256"
Write-Host "Approve it for a host, then restart that host, e.g.:"
Write-Host "  dotnet user-secrets set Plugins:AllowedSha256 $sha256 --project src/AgentOs.Web"
Write-Host "  (or the environment variable Plugins__AllowedSha256=$sha256; several hashes: separate with ';')"
Write-Host "Done. Restart the host to load the plugin."
