<#
  GPD Forge - run the service elevated with hardware writes ENABLED. GPL-3.0-or-later.

  Launches the daemon with GPDFORGE_ENABLE_HARDWARE=1 so POST /tdp drives RyzenAdj for real.
  The service never changes power on its own - only an explicit POST /tdp does. GPD Tool / MA
  running at the same time will fight real TDP changes (close them for a real change).

  Read-only telemetry and the API work the same as the normal service; only /tdp becomes live.
#>
[CmdletBinding()]
param(
    [string]$RyzenAdj = "C:\Program Files\Motion Assistant\amd\ryzenadj.exe",
    [switch]$Release
)
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevation required - relaunching (accept UAC)..." -ForegroundColor Yellow
    $fwd = @()
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        if ($kv.Value -is [switch]) { if ($kv.Value.IsPresent) { $fwd += "-$($kv.Key)" } }
        else { $fwd += "-$($kv.Key)"; $fwd += "$($kv.Value)" }
    }
    $argList = @('-NoExit','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"") + $fwd
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    return
}

$repo = Split-Path $PSScriptRoot -Parent
$conf = if ($Release) { 'Release' } else { 'Debug' }
$dll  = Join-Path $repo "core\bin\$conf\net9.0-windows\GpdForge.Service.dll"
if (-not (Test-Path $dll)) { Write-Host "Build first: dotnet build core/GpdForge.Service.csproj -c $conf" -ForegroundColor Red; return }

$conflicts = Get-Process MotionAssistant,pmgui,GPDTool,GPDToolService -ErrorAction SilentlyContinue | Select-Object -Expand ProcessName -Unique
if ($conflicts) { Write-Host "[!] Running power controller(s): $($conflicts -join ', ') - real /tdp changes will conflict. /telemetry is unaffected.`n" -ForegroundColor Yellow }

$env:GPDFORGE_ENABLE_HARDWARE = '1'
$env:GPDFORGE_RYZENADJ = $RyzenAdj
Write-Host "Starting GPD Forge service (hardware ENABLED) on http://127.0.0.1:8787 ... Ctrl+C to stop." -ForegroundColor Cyan
& dotnet $dll
