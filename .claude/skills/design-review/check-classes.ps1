#!/usr/bin/env pwsh
# AgentOS.NET design-review — phantom CSS class detector.
#
# Lists class tokens referenced in .razor (static class="..." attributes) that are defined in NO
# CSS the app ships (wwwroot/*.css + the scoped-css bundle under obj/). Phantom classes render
# unstyled — they were the root cause of the Spine/Users layout breakage (admin-page / admin-section
# / admin-tbl / admin-invite were used in markup but defined nowhere).
#
# Heuristic, not a proof: covers static class="..." attrs. Fully-interpolated names (class="@(...)")
# are not parsed, and a hit may be a third-party class whose CSS ships separately (e.g. Z.Blazor
# .Diagrams). Review the hits — don't auto-trust or auto-fail CI on it yet.
#
# Usage:  pwsh .claude/skills/design-review/check-classes.ps1
# Exit:   0 = clean, 1 = phantom tokens found.

param([string]$WebRoot = "src/AgentOs.Web")

$ErrorActionPreference = 'Stop'

# 1) Defined classes — every CSS the app actually ships (over-collecting here only risks MISSING a
#    phantom, never a false alarm, so it is the safe direction).
$cssFiles = @()
$cssFiles += Get-ChildItem -Path "$WebRoot/wwwroot" -Filter *.css -Recurse -ErrorAction SilentlyContinue
$cssFiles += Get-ChildItem -Path "$WebRoot/obj" -Filter *.css -Recurse -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match 'scopedcss' }

$defined = [System.Collections.Generic.HashSet[string]]::new()
foreach ($f in $cssFiles) {
  $text = Get-Content $f.FullName -Raw
  foreach ($m in [regex]::Matches($text, '\.([a-z][a-z0-9_-]*)')) {
    [void]$defined.Add($m.Groups[1].Value)
  }
}

# 2) Used classes — static class="..." attributes in .razor.
$used = @{}
foreach ($f in (Get-ChildItem -Path $WebRoot -Filter *.razor -Recurse)) {
  $text = Get-Content $f.FullName -Raw
  foreach ($m in [regex]::Matches($text, 'class="([^"]*)"')) {
    foreach ($tok in ($m.Groups[1].Value -split '\s+')) {
      if ($tok -match '^[a-z][a-z0-9_-]*$') {
        if (-not $used.ContainsKey($tok)) { $used[$tok] = [System.Collections.Generic.HashSet[string]]::new() }
        [void]$used[$tok].Add($f.Name)
      }
    }
  }
}

# 3) Report tokens used but never defined.
$phantom = $used.Keys | Where-Object { -not $defined.Contains($_) } | Sort-Object
if (-not $phantom) {
  Write-Host "OK - no phantom classes. $($defined.Count) defined; all static .razor class tokens resolve."
  exit 0
}
Write-Host "PHANTOM CLASSES (used in .razor, defined in no CSS):"
foreach ($p in $phantom) { "{0,-28} <- {1}" -f $p, (($used[$p]) -join ', ') }
Write-Host ""
Write-Host "$($phantom.Count) phantom token(s). Fix: switch to the defined vocab (prefs-*, .page-head,"
Write-Host ".settings-table, Panel/Btn/Field) OR add the class to app.css using var(--...) tokens only."
exit 1
