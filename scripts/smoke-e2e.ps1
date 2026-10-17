#requires -Version 7
<#
.SYNOPSIS
  End-to-end smoke for AgentOS.NET. Two modes:
    (default)   boot the STANDALONE Web (no DB) and health-probe it — automated proof the app boots +
                serves the desktop. Then prints the runbook for the parts that need real services.
    -FullStack  print the full Aspire-stack runbook (Postgres + Keycloak + Api + Web) + the click-by-click
                checklist to PROVE the headline flows with a real LLM key (+ paired runner for the spine).

  The "real product" gap is not a phase number — it is ONE full-stack run with a real LLM key. This script
  is the guided way to do it.

.EXAMPLE
  pwsh scripts/smoke-e2e.ps1
  pwsh scripts/smoke-e2e.ps1 -FullStack
#>
param(
    [switch]$FullStack,
    [int]$Port = 5180,
    [int]$TimeoutSec = 90
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Section($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok($t)      { Write-Host "  [ok]   $t" -ForegroundColor Green }
function Warn($t)    { Write-Host "  [warn] $t" -ForegroundColor Yellow }
function Step($n,$t) { Write-Host "  $n. $t" }

Section "Prerequisites"
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if ($dotnet) { Ok "dotnet $(dotnet --version)" } else { Warn "dotnet not found on PATH" }
$llmKey = $false
try { Push-Location src/AgentOs.Api; $secrets = dotnet user-secrets list 2>$null; Pop-Location
      if ($secrets -match 'Llm:Claude:ApiKey|Llm:AzureOpenAi:ApiKey') { $llmKey = $true } } catch {}
if ($llmKey) { Ok "an LLM API key is set in user-secrets (real pipeline runs will work)" }
else { Warn "no LLM key in user-secrets — the 5-agent Pipeline can't produce real output until you set one:
           cd src/AgentOs.Api; dotnet user-secrets set 'Llm:Claude:ApiKey' 'sk-ant-...'" }

if ($FullStack) {
    Section "Full Aspire stack — the real product run"
    Step 1 "Boot everything (Postgres + Keycloak + MailHog + Api + Web):"
    Write-Host "       dotnet run --project infra/AgentOs.AppHost" -ForegroundColor White
    Step 2 "Aspire dashboard: http://localhost:15050   Web: https://localhost:5180   (login operator / operator, realm agentic)"
    Step 3 "PROVE the 5-agent Pipeline (needs the LLM key): open the *Pipeline* app -> enter a user story -> Run."
    Write-Host "         success = Requirement -> Coding -> Testing -> QA stages stream live and produce artifacts;" -ForegroundColor DarkGray
    Write-Host "         the *Cost* app then shows real per-agent spend, and *Prompts*/*Policy*/*Budget* apply to it." -ForegroundColor DarkGray
    Step 4 "PROVE the spine (needs a paired runner + a real GitHub Projects board + PAT repo+read:project+write:project):"
    Write-Host "         pair a runner (VS Code extension or the AgentOs.RemoteAgent exe) -> Spine -> Connect a board ->" -ForegroundColor DarkGray
    Write-Host "         Start from idea -> tick 'Run on my machine' (Claude or Codex) -> Create & run -> watch a real PR open." -ForegroundColor DarkGray
    Step 5 "gen_ai telemetry: the Aspire dashboard traces show 'chat {model}' spans + the agentos.llm.cost.usd metric."
    Write-Host "`n  That sequence is the line between 'compiles + unit-green' and 'proven product'. Run it once." -ForegroundColor Cyan
    return
}

Section "Standalone Web smoke (no DB) — automated boot + probe"
$url = "http://localhost:$Port"
Warn "starting the Web (degraded path: no-op repos, dev auto-login)…"
$log = Join-Path ([System.IO.Path]::GetTempPath()) "agentos-smoke.log"
$proc = Start-Process dotnet `
    -ArgumentList 'run','--project','src/AgentOs.Web','--','--Persistence:RequireDatabase=false',"--urls",$url `
    -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest "$url/health" -SkipCertificateCheck -TimeoutSec 5 -ErrorAction Stop
            if ($r.StatusCode -eq 200) { $healthy = $true; break }
        } catch { Start-Sleep -Seconds 2 }
    }
    if ($healthy) {
        Ok "GET /health -> 200"
        $page = Invoke-WebRequest $url -SkipCertificateCheck -TimeoutSec 10
        if ($page.Content -match 'AgentOS.NET') { Ok "desktop served (title/content contains 'AgentOS.NET')" }
        else { Warn "home page served but 'AgentOS.NET' marker not found" }
        Write-Host "`n  Standalone boots + serves. This proves render + the degraded path ONLY — persistence," -ForegroundColor Cyan
        Write-Host "  real cost/budget/policy, and the idea->PR spine need the full stack: re-run with -FullStack." -ForegroundColor Cyan
    } else {
        Warn "Web did not become healthy within ${TimeoutSec}s. Last log lines:"
        if (Test-Path $log) { Get-Content $log -Tail 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray } }
        exit 1
    }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; Warn "stopped the Web (pid $($proc.Id))" }
}
