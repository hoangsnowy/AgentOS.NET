#!/usr/bin/env pwsh
<#
.SYNOPSIS
    AgentOS.NET lab server — a long-running, isolated full stack for real GitHub / Azure DevOps / LLM testing.

.DESCRIPTION
    Runs the Aspire AppHost with the "lab" profile from its OWN git worktree (default ..\AgentOs-lab), so the
    lab keeps running while you rebuild the dev checkout. Lab ports never collide with the dev stack:

        Desktop  https://lab.dev.localhost:6180   (login operator / operator)
        Api      https://localhost:6080
        Keycloak https://labkc.dev.localhost:9080    MailHog http://localhost:9025
        Aspire   https://localhost:18050

    Commands:
        up       create the worktree if needed, build, start in the background, wait until healthy
        update   stop, move the worktree to the latest origin/main (or -Ref), rebuild, start
        down     stop the lab
        status   show health + URLs
        reset    stop and DELETE the lab's data volumes (Postgres + Keycloak) — asks unless -Force
        logs     tail the AppHost log
        test     run the lab integration tests (real GitHub / Azure DevOps) + the real-auth UI E2E against the lab
        pair     pair THIS machine as a RemoteAgent runner (claude -p, your Claude subscription) and route the lab
                 tenant's agents to it — real agent runs with no API key on the server (prompt mode, no YOLO;
                 Board "Run on my machine" sessions get scoped autonomy: own dir per run + a tool allowlist)
        unpair   stop the paired runner

.EXAMPLE
    pwsh infra/lab/lab.ps1 up
    pwsh infra/lab/lab.ps1 update -Ref origin/feat/my-branch
#>
param(
    [Parameter(Position = 0)][ValidateSet('up', 'update', 'down', 'status', 'reset', 'logs', 'test', 'pair', 'unpair')][string]$Command = 'status',
    [string]$Ref = 'origin/main',
    [string]$LabDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '..\AgentOs-lab'),
    [switch]$Force,
    # pair: test data the agents may read on this computer, e.g. "CHESS_LIBRARY_PATH=D:\Chess" (REMOTE_AGENT_DATA)
    [string]$Data
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$LabDir = [System.IO.Path]::GetFullPath($LabDir)
$StateDir = Join-Path $LabDir '.lab'
$PidFile = Join-Path $StateDir 'apphost.pid'
$LogFile = Join-Path $StateDir 'apphost.log'
$WebUrl = 'https://lab.dev.localhost:6180'
$KeycloakUrl = 'https://labkc.dev.localhost:9080'

function Test-Web {
    # Healthy = the login endpoint answers 302 to Keycloak. curl.exe (ships with Windows 10+) because
    # Invoke-WebRequest reports a non-followed redirect as an error.
    $code = & curl.exe -k -s -o NUL -w '%{http_code}' --max-time 5 "$WebUrl/account/login" 2>$null
    return $code -eq '302'
}

function Get-LabProcess {
    if (-not (Test-Path $PidFile)) { return $null }
    $id = [int](Get-Content $PidFile)
    return Get-Process -Id $id -ErrorAction SilentlyContinue
}

function Stop-Runner {
    $runnerPid = Join-Path $StateDir 'runner.pid'
    if (-not (Test-Path $runnerPid)) { return }
    $p = Get-Process -Id ([int](Get-Content $runnerPid)) -ErrorAction SilentlyContinue
    if ($p) { Write-Host "Stopping paired runner (pid $($p.Id))…"; Stop-Process -Id $p.Id -Force }
    Remove-Item $runnerPid -Force
}

function Stop-Lab {
    $p = Get-LabProcess
    if ($p) {
        Write-Host "Stopping lab (pid $($p.Id))…"
        # The AppHost owns DCP + child processes; kill the whole tree.
        & taskkill /T /F /PID $p.Id | Out-Null
    }
    Remove-Item $PidFile -ErrorAction SilentlyContinue
    Remove-OrphanTunnelProxies
}

# A hard-killed AppHost never removes its Aspire network tunnel-proxy containers. They piled up (77 on this box: every
# lab update, plus every AppHost a test or preview run started) until a fresh AppHost created no containers at all and
# the lab never became healthy. Each one records the process that created it; remove those whose creator is gone.
# Only aspire-container-network-tunnelproxy-* containers are touched — never databases, volumes or anyone else's.
function Remove-OrphanTunnelProxies {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return }
    $names = docker ps -a --format '{{.Names}}' 2>$null | Where-Object { $_ -like 'aspire-container-network-tunnelproxy-*' }
    $orphans = foreach ($n in $names) {
        $label = docker inspect $n --format '{{index .Config.Labels "com.microsoft.developer.usvc-dev.creatorProcessId"}}|{{index .Config.Labels "com.microsoft.developer.usvc-dev.creatorProcessStartTime"}}'
        $parts = "$label".Split('|')
        $creatorId = 0
        if ($parts.Count -ne 2 -or -not [int]::TryParse($parts[0], [ref]$creatorId)) { continue }
        $creator = Get-Process -Id $creatorId -ErrorAction SilentlyContinue
        $started = [datetime]::MinValue
        $alive = $creator -and [datetime]::TryParse($parts[1], [ref]$started) -and [math]::Abs(($creator.StartTime - $started).TotalSeconds) -lt 2
        if (-not $alive) { $n }
    }
    if ($orphans) {
        Write-Host "Removing $(@($orphans).Count) orphaned Aspire tunnel-proxy container(s)…"
        docker rm -f @($orphans) | Out-Null
    }
}

function Ensure-Worktree {
    if (-not (Test-Path (Join-Path $LabDir '.git'))) {
        Write-Host "Creating lab worktree at $LabDir ($Ref)…"
        git -C $RepoRoot fetch --quiet origin
        git -C $RepoRoot worktree add --detach $LabDir $Ref
    }
    New-Item -ItemType Directory -Force $StateDir | Out-Null
}

function Start-Lab {
    if (Get-LabProcess) { Write-Host 'Lab already running.'; return }
    Write-Host 'Building lab…'
    dotnet build (Join-Path $LabDir 'infra/AgentOs.AppHost') -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Lab build failed.' }
    $p = Start-Process dotnet -PassThru -WindowStyle Hidden -WorkingDirectory $LabDir `
        -ArgumentList @('run', '--project', 'infra/AgentOs.AppHost', '--launch-profile', 'lab', '--no-build') `
        -RedirectStandardOutput $LogFile -RedirectStandardError "$LogFile.err"
    $p.Id | Set-Content $PidFile
    Write-Host -NoNewline 'Waiting for the lab to be healthy'
    for ($i = 0; $i -lt 90; $i++) {
        if (Test-Web) { Write-Host ''; Sync-LabRealm; Show-Status; return }
        Write-Host -NoNewline '.'
        Start-Sleep -Seconds 5
    }
    throw "Lab did not become healthy — see $LogFile"
}

function Sync-LabRealm {
    # Keycloak imports the realm only on first start, so an existing lab realm never picks up changes to
    # agentic-realm.json. Make sure the agentic-web client accepts the lab desktop origin (idempotent).
    $kcAdmin = 'https://localhost:9080'
    try {
        $token = (Invoke-RestMethod -SkipCertificateCheck -Method Post "$kcAdmin/realms/master/protocol/openid-connect/token" `
            -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'admin'; password = 'admin' }).access_token
        $headers = @{ Authorization = "Bearer $token" }
        $client = (Invoke-RestMethod -SkipCertificateCheck -Headers $headers "$kcAdmin/admin/realms/agentic/clients?clientId=agentic-web")[0]
        $redirect = "$WebUrl/*"
        if ($client.redirectUris -notcontains $redirect -or $client.webOrigins -notcontains $WebUrl) {
            $client.redirectUris = @($client.redirectUris + $redirect | Select-Object -Unique)
            $client.webOrigins = @($client.webOrigins + $WebUrl | Select-Object -Unique)
            Invoke-RestMethod -SkipCertificateCheck -Method Put -Headers $headers -ContentType 'application/json' `
                "$kcAdmin/admin/realms/agentic/clients/$($client.id)" -Body ($client | ConvertTo-Json -Depth 20) | Out-Null
            Write-Host "Realm: agentic-web now accepts $WebUrl"
        }
    } catch {
        Write-Warning "Could not sync the lab realm ($($_.Exception.Message)). Sign-in may fail until it is synced."
    }
}

function Show-Status {
    $p = Get-LabProcess
    $commit = if (Test-Path (Join-Path $LabDir '.git')) { git -C $LabDir log -1 --format='%h %s' } else { '(no worktree)' }
    Write-Host "Lab worktree : $LabDir"
    Write-Host "Lab commit   : $commit"
    Write-Host "Process      : $(if ($p) { "running (pid $($p.Id))" } else { 'stopped' })"
    Write-Host "Desktop      : $WebUrl  ($(if (Test-Web) { 'healthy' } else { 'down' }))  login operator / operator"
    Write-Host 'Aspire       : https://localhost:18050    Api: https://localhost:6080    MailHog: http://localhost:9025'
}

switch ($Command) {
    'up' { Ensure-Worktree; Start-Lab }
    'update' {
        Ensure-Worktree; Stop-Lab
        git -C $LabDir fetch --quiet origin
        git -C $LabDir checkout --quiet --detach $Ref
        Start-Lab
    }
    'down' { Stop-Runner; Stop-Lab; Write-Host 'Lab stopped.' }
    'pair' {
        if (-not (Test-Web)) { throw "Lab is not running — start it with: pwsh infra/lab/lab.ps1 up" }
        if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { throw 'claude CLI not on PATH — install Claude Code and sign in first.' }
        Stop-Runner
        Write-Host '== Building the runner + Playwright from the lab worktree'
        dotnet build (Join-Path $LabDir 'src/AgentOs.RemoteAgent') -c Release | Out-Null
        $pwDir = Join-Path $LabDir 'tests/AgentOs.E2E.Tests/bin/Release/net10.0/.playwright'
        if (-not (Test-Path $pwDir)) { dotnet build (Join-Path $LabDir 'tests/AgentOs.E2E.Tests') -c Release | Out-Null }
        $env:PW_PACKAGE = Join-Path $pwDir 'package'
        $env:LAB_URL = $WebUrl
        $env:RUNNER_EXE = Join-Path $LabDir 'src/AgentOs.RemoteAgent/bin/Release/net10.0/AgentOs.RemoteAgent.exe'
        $env:STATE_DIR = $StateDir
        if ($Data) { $env:REMOTE_AGENT_DATA = $Data; Write-Host "== Test data for the agents: $(($Data -split ';' | ForEach-Object { ($_ -split '=')[0] }) -join ', ')" }
        else { Remove-Item Env:REMOTE_AGENT_DATA -ErrorAction SilentlyContinue }
        & (Join-Path $pwDir 'node/win32_x64/node.exe') (Join-Path $PSScriptRoot 'pair-runner.js')
        if ($LASTEXITCODE -ne 0) { throw 'Pairing failed — see the message above.' }
        Start-Sleep -Seconds 4
        Get-Content (Join-Path $StateDir 'runner.log') -Tail 3
    }
    'unpair' {
        Stop-Runner
        if (Test-Web) {
            $pwDir = Join-Path $LabDir 'tests/AgentOs.E2E.Tests/bin/Release/net10.0/.playwright'
            $env:PW_PACKAGE = Join-Path $pwDir 'package'; $env:LAB_URL = $WebUrl; $env:UNPAIR = '1'
            & (Join-Path $pwDir 'node/win32_x64/node.exe') (Join-Path $PSScriptRoot 'pair-runner.js')
            Remove-Item Env:UNPAIR
        }
        Write-Host 'Runner stopped. (Disconnect it in Board → Computers to retire its token.)'
    }
    'status' { Show-Status }
    'logs' { Get-Content $LogFile -Tail 80 -Wait }
    'test' {
        if (-not (Test-Web)) { throw "Lab is not running — start it with: pwsh infra/lab/lab.ps1 up" }
        # The lab Postgres is the container mounting the lab volume; its host port is dynamic.
        $pg = docker ps --format '{{.Names}}' | Where-Object { $_ -like 'postgres-*' } |
            Where-Object { (docker inspect $_ --format '{{range .Mounts}}{{.Name}} {{end}}') -match 'agentos-lab-postgres-data' } |
            Select-Object -First 1
        $pgPort = if ($pg) { ((docker port $pg 5432) | Select-Object -First 1).Split(':')[-1] } else { $null }
        $env:RUN_AGENTOS_LAB = 'true'
        $env:RUN_AGENTOS_E2E_REAL = 'true'
        $env:AGENTOS_REAL_URL = $WebUrl
        $env:MAILHOG_URL = 'http://localhost:9025'
        $env:AGENTOS_KC_URL = 'https://localhost:9080'   # Keycloak admin API, for tests that clean up users they create
        if ($pgPort) { $env:AGENTOS_PG_CONN = "Host=localhost;Port=$pgPort;Database=agentos;Username=postgres;Password=postgres" }
        Write-Host '== Lab integration tests (real GitHub / Azure DevOps)'
        # Tests run from the lab worktree: same code as the running lab, own build output (no dev-checkout locks).
        dotnet test --project (Join-Path $LabDir 'tests/AgentOs.Lab.Tests') -c Release
        Write-Host '== Real-auth UI E2E against the lab'
        dotnet test --project (Join-Path $LabDir 'tests/AgentOs.E2E.Tests') -c Release
    }
    'reset' {
        if (-not $Force) {
            $answer = Read-Host 'This deletes the lab Postgres + Keycloak volumes (all lab data). Type "reset" to continue'
            if ($answer -ne 'reset') { Write-Host 'Cancelled.'; return }
        }
        Stop-Lab
        docker volume rm agentos-lab-postgres-data agentos-lab-keycloak-data 2>$null
        Write-Host 'Lab data removed. Run "up" to start fresh.'
    }
}
