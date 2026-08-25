<#
  GPD Forge - supervised live TDP tool (RyzenAdj). GPL-3.0-or-later.

  SAFE-BY-DEFAULT. Designed to run on a machine that has OTHER work in progress
  (e.g. active agent sessions): it never changes net power unless you explicitly ask,
  it verifies every write by reading the PM table back, and it auto-reverts.

  Actions (pick one):
    -Read              (default) read current TDP via "ryzenadj --info" and print it. No write.
    -ReassertCurrent   write the CURRENT STAPM/FAST/SLOW back (proves the write path with
                       ZERO net change - safe even while GPD Tool is managing power).
    -TargetW <n>       apply n W, verify, hold -Seconds, then REVERT to the original.
                       Requires -Confirm and that no other power controller is running.

  Options:
    -Seconds <s>       hold time before auto-revert for -TargetW (default 20).
    -Confirm           required to actually change power with -TargetW.
    -RyzenAdj <path>   override ryzenadj.exe path.

  Examples:
    powershell -ExecutionPolicy Bypass -File scripts\tdp-live.ps1
    powershell -ExecutionPolicy Bypass -File scripts\tdp-live.ps1 -ReassertCurrent
    powershell -ExecutionPolicy Bypass -File scripts\tdp-live.ps1 -TargetW 18 -Seconds 15 -Confirm
#>
[CmdletBinding()]
param(
    [switch]$Read,
    [switch]$ReassertCurrent,
    [int]$TargetW = 0,
    [int]$Seconds = 20,
    [switch]$Confirm,
    [string]$RyzenAdj = "C:\Program Files\Motion Assistant\amd\ryzenadj.exe"
)

$ErrorActionPreference = 'Stop'

# --- self-elevate (needed to load ryzenadj driver) ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevation required - relaunching as administrator (accept the UAC prompt)..." -ForegroundColor Yellow
    $fwd = @()
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        if ($kv.Value -is [switch]) { if ($kv.Value.IsPresent) { $fwd += "-$($kv.Key)" } }
        else { $fwd += "-$($kv.Key)"; $fwd += "$($kv.Value)" }
    }
    $argList = @('-NoExit','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"") + $fwd
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    return
}

if (-not (Test-Path $RyzenAdj)) { Write-Host "ryzenadj not found at $RyzenAdj - pass -RyzenAdj <path>." -ForegroundColor Red; return }

# --- conflict detection: another power controller fighting us collapses performance ---
$conflicts = Get-Process MotionAssistant,pmgui,GPDTool,GPDToolService -ErrorAction SilentlyContinue | Select-Object -Expand ProcessName -Unique
if ($conflicts) {
    Write-Host "[!] Power controller(s) running: $($conflicts -join ', ')." -ForegroundColor Yellow
    Write-Host "    They re-apply TDP on their own and WILL fight a real change (the MA + GPD Tool clash)." -ForegroundColor Yellow
    Write-Host "    -Read and -ReassertCurrent are safe anyway. For -TargetW, close them first.`n" -ForegroundColor Yellow
}

function Read-Tdp {
    $info = & $RyzenAdj --info 2>&1 | Out-String
    $stapm = if ($info -match '(?im)STAPM\s*LIMIT.*?([-+]?\d+(?:\.\d+)?)') { [int][math]::Round([double]$Matches[1]) } else { 0 }
    $fast  = if ($info -match '(?im)PPT\s*LIMIT\s*FAST.*?([-+]?\d+(?:\.\d+)?)') { [int][math]::Round([double]$Matches[1]) } else { 0 }
    $slow  = if ($info -match '(?im)PPT\s*LIMIT\s*SLOW.*?([-+]?\d+(?:\.\d+)?)') { [int][math]::Round([double]$Matches[1]) } else { 0 }
    [pscustomobject]@{ Stapm = $stapm; Fast = $fast; Slow = $slow; Raw = $info }
}

function Apply-Tdp([int]$stapm,[int]$fast,[int]$slow) {
    & $RyzenAdj "--stapm-limit=$($stapm*1000)" "--fast-limit=$($fast*1000)" "--slow-limit=$($slow*1000)" | Out-Null
    Start-Sleep -Milliseconds 400
}

$log = Join-Path $PSScriptRoot "logs"; New-Item -ItemType Directory -Force -Path $log | Out-Null
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')

Write-Host "Reading current TDP..." -ForegroundColor Cyan
$before = Read-Tdp
Write-Host ("  STAPM={0}W  FAST={1}W  SLOW={2}W" -f $before.Stapm,$before.Fast,$before.Slow) -ForegroundColor Green

if ($ReassertCurrent) {
    if ($before.Stapm -le 0) { Write-Host "Could not read current STAPM - aborting (will not write blind)." -ForegroundColor Red; return }
    Write-Host "Re-asserting current values (zero net change)..." -ForegroundColor Cyan
    Apply-Tdp $before.Stapm $before.Fast $before.Slow
    $after = Read-Tdp
    $ok = [math]::Abs($after.Stapm - $before.Stapm) -le 1
    $verdict = if ($ok) { 'VERIFIED (write path works)' } else { 'UNVERIFIED (firmware/controller reverted)' }
    Write-Host ("  read-back STAPM={0}W  -> {1}" -f $after.Stapm, $verdict) -ForegroundColor $(if($ok){'Green'}else{'Yellow'})
    "$stamp reassert before=$($before.Stapm) after=$($after.Stapm) ok=$ok" | Add-Content "$log\tdp-live.log"
    return
}

if ($TargetW -gt 0) {
    if (-not $Confirm) { Write-Host "Refusing to change power without -Confirm. (Add -Confirm to proceed.)" -ForegroundColor Red; return }
    if ($conflicts)   { Write-Host "Refusing -TargetW while $($conflicts -join ', ') is running - close it first." -ForegroundColor Red; return }
    if ($TargetW -lt 5 -or $TargetW -gt 35) { Write-Host "TargetW must be 5..35." -ForegroundColor Red; return }

    Write-Host ("Applying STAPM={0}W for {1}s, then reverting to {2}W..." -f $TargetW,$Seconds,$before.Stapm) -ForegroundColor Cyan
    Apply-Tdp $TargetW $TargetW ([math]::Max($TargetW,$before.Slow))
    $mid = Read-Tdp
    $ok = [math]::Abs($mid.Stapm - $TargetW) -le 1
    $verdict = if ($ok) { 'VERIFIED' } else { 'UNVERIFIED (reverted by firmware)' }
    Write-Host ("  read-back STAPM={0}W -> {1}" -f $mid.Stapm, $verdict) -ForegroundColor $(if($ok){'Green'}else{'Yellow'})
    Start-Sleep -Seconds $Seconds
    Write-Host "Reverting to original..." -ForegroundColor Cyan
    Apply-Tdp $before.Stapm $before.Fast $before.Slow
    $end = Read-Tdp
    Write-Host ("  restored STAPM={0}W" -f $end.Stapm) -ForegroundColor Green
    "$stamp target=$TargetW mid=$($mid.Stapm) ok=$ok restored=$($end.Stapm)" | Add-Content "$log\tdp-live.log"
    return
}

Write-Host "`n(Read-only. Use -ReassertCurrent to prove the write path safely, or -TargetW <n> -Confirm to test a value.)" -ForegroundColor DarkGray
