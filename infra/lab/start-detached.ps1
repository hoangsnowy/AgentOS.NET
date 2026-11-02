#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Start a long-running lab process (the AppHost, the paired runner) so it OUTLIVES the shell that started it.

.DESCRIPTION
    A child started with Start-Process or node's spawn({ detached: true }) stays in its parent's Windows job object. When
    that shell runs inside a job with kill-on-close — a Claude Code session's Bash/PowerShell tool, a CI step, some
    terminals — ending the session kills the lab and the runner with it, and the lab then sits "down" for hours.

    On Windows this starts the process through WMI (Win32_Process.Create): WmiPrvSE creates it, so it is in none of the
    caller's jobs and survives the caller. A WMI-created process does not inherit the caller's environment, so a clean
    block is handed over in the same call (Win32_ProcessStartup.EnvironmentVariables): the user's logon environment
    (registry Machine + User, Path = Machine;User) plus only the lab's own variables (REMOTE_AGENT_*, PW_*, LAB_*,
    ASPNETCORE_*, DOTNET_*, …) — never the caller's shell session (MSYS / Claude Code variables broke the runner's tools).
    It travels in memory only: a pairing token in it never reaches a command line or a file. Output redirection lives in a
    small generated launcher (<StateDir>\<Name>.launch.cmd: working directory, command, log paths — never environment
    values), and the pid file gets the REAL process's pid (the launcher cmd's child), so stop / status keep working.

    Elsewhere (no WMI) it falls back to Start-Process, which does not survive a job with kill-on-close.

    Prints the started process's pid.
#>
param(
    [Parameter(Mandatory)][string]$FilePath,
    [string[]]$ArgumentList = @(),
    [Parameter(Mandatory)][string]$WorkingDirectory,
    # Where stdout goes; stderr goes to -StdErr, or to the same file when it is omitted.
    [Parameter(Mandatory)][string]$StdOut,
    [string]$StdErr,
    # Append to the log(s) instead of replacing them.
    [switch]$Append,
    [Parameter(Mandatory)][string]$PidFile,
    # Launcher name: <directory of PidFile>\<Name>.launch.cmd
    [Parameter(Mandatory)][string]$Name
)
$ErrorActionPreference = 'Stop'

$resolved = Get-Command $FilePath -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$exe = if ($resolved) { $resolved.Source } else { $FilePath }
$pidDir = Split-Path -Parent ([System.IO.Path]::GetFullPath($PidFile))
New-Item -ItemType Directory -Force $pidDir | Out-Null

function Test-Wmi {
    if (-not $IsWindows) { return $false }
    try { $null = Get-CimClass -ClassName Win32_Process -ErrorAction Stop; return $true } catch { return $false }
}

if (-not (Test-Wmi)) {
    Write-Warning "WMI is not available: $Name starts as a child of this shell and stops when the shell's session ends."
    $splat = @{ FilePath = $exe; PassThru = $true; WorkingDirectory = $WorkingDirectory; RedirectStandardOutput = $StdOut }
    if ($ArgumentList.Count) { $splat.ArgumentList = $ArgumentList }
    if ($StdErr) { $splat.RedirectStandardError = $StdErr } else { $splat.RedirectStandardError = "$StdOut.err" }
    if ($IsWindows) { $splat.WindowStyle = 'Hidden' }
    $p = Start-Process @splat
    $p.Id | Set-Content $PidFile
    return $p.Id
}

# cmd.exe syntax: every path/argument double-quoted when it needs it; '%' doubled so a batch file never expands it.
function ConvertTo-CmdToken([string]$s) {
    $s = $s.Replace('%', '%%')
    if ($s -match '[\s&|<>^(),;=!"]' -or $s.Length -eq 0) { return '"' + $s.Replace('"', '""') + '"' }
    return $s
}
$redirect = if ($Append) { '>>' } else { '>' }
$errPart = if ($StdErr) { "2$redirect $(ConvertTo-CmdToken $StdErr)" } else { '2>&1' }
$commandLine = (@($exe) + $ArgumentList | ForEach-Object { ConvertTo-CmdToken $_ }) -join ' '
$launcher = Join-Path $pidDir "$Name.launch.cmd"
# UTF-8 (chcp 65001) so a non-ASCII path in this file parses. Environment values are NOT written here.
@(
    '@echo off'
    'chcp 65001 >nul'
    "cd /d $(ConvertTo-CmdToken $WorkingDirectory)"
    "$commandLine $redirect $(ConvertTo-CmdToken $StdOut) $errPart"
) -join "`r`n" | Set-Content -Path $launcher -Encoding utf8NoBOM -NoNewline

# A clean environment, in memory only: what a fresh logon gives this user (Machine + User registry variables, Path =
# Machine;User, expanded) plus the process variables Windows sets at logon, plus only the variables the lab itself
# passes on (runner pairing, Playwright, ASP.NET/.NET settings). Handing over the caller's WHOLE block broke the runner:
# a Git Bash / Claude Code shell carries MSYS and session variables and its own Path spelling, and the WMI-created
# runner's tools then failed ("git" not found in Bash, "The command line is too long" in PowerShell).
function Get-LabEnvironment {
    $vars = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($scope in 'Machine', 'User') {
        $block = [System.Environment]::GetEnvironmentVariables($scope)
        foreach ($key in $block.Keys) {
            if ("$key" -ieq 'Path') { continue }
            $vars["$key"] = [System.Environment]::ExpandEnvironmentVariables([string]$block[$key])
        }
    }
    $paths = @('Machine', 'User') | ForEach-Object { [System.Environment]::GetEnvironmentVariable('Path', $_) } | Where-Object { $_ }
    $vars['Path'] = [System.Environment]::ExpandEnvironmentVariables(($paths -join ';'))
    $logon = 'SystemRoot', 'windir', 'SystemDrive', 'COMPUTERNAME', 'USERNAME', 'USERDOMAIN', 'USERDOMAIN_ROAMINGPROFILE',
        'USERPROFILE', 'HOMEDRIVE', 'HOMEPATH', 'APPDATA', 'LOCALAPPDATA', 'ProgramData', 'ALLUSERSPROFILE', 'PUBLIC',
        'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'CommonProgramFiles', 'CommonProgramFiles(x86)',
        'CommonProgramW6432', 'PROCESSOR_ARCHITECTURE', 'PROCESSOR_IDENTIFIER', 'NUMBER_OF_PROCESSORS', 'OS', 'PATHEXT',
        'ComSpec', 'SESSIONNAME', 'LOGONSERVER'
    foreach ($key in $logon) {
        $value = [System.Environment]::GetEnvironmentVariable($key)
        if ($value -and -not $vars.ContainsKey($key)) { $vars[$key] = $value }
    }
    $passOn = '^(REMOTE_AGENT_|AGENTOS_|LAB_|PW_|ASPNETCORE_|DOTNET_|NUGET_|CLAUDE_CODE_GIT_BASH_PATH$|STATE_DIR$|RUNNER_EXE$)'
    foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
        if ("$($entry.Key)" -match $passOn) { $vars["$($entry.Key)"] = [string]$entry.Value }
    }
    return $vars
}
$environment = [string[]]@((Get-LabEnvironment).GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
$startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{
    ShowWindow           = [uint16]0   # SW_HIDE: no console window on the desktop
    EnvironmentVariables = $environment
}
$cmd = Join-Path ([System.Environment]::SystemDirectory) 'cmd.exe'
$result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine               = "`"$cmd`" /d /s /c `"`"$launcher`"`""
    CurrentDirectory          = $WorkingDirectory
    ProcessStartupInformation = $startup
}
if ($result.ReturnValue -ne 0) { throw "Could not start $Name through WMI (Win32_Process.Create returned $($result.ReturnValue))." }
$launcherPid = [int]$result.ProcessId

# Record the real process, not the launcher: the launcher cmd's child running $exe.
$exeName = [System.IO.Path]::GetFileName($exe)
if (-not [System.IO.Path]::HasExtension($exeName)) { $exeName += '.exe' }
for ($i = 0; $i -lt 100; $i++) {
    $child = Get-CimInstance Win32_Process -Filter "ParentProcessId = $launcherPid" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $exeName } | Select-Object -First 1
    if ($child) {
        [int]$child.ProcessId | Set-Content $PidFile
        return [int]$child.ProcessId
    }
    if (-not (Get-Process -Id $launcherPid -ErrorAction SilentlyContinue)) {
        throw "$Name exited right after it started (launcher $launcher) — see $StdOut."
    }
    Start-Sleep -Milliseconds 200
}
# Still starting but not found by name: track the launcher instead — stopping its tree stops the process too.
Write-Warning "Could not find $exeName under the launcher (pid $launcherPid); tracking the launcher instead."
$launcherPid | Set-Content $PidFile
return $launcherPid
