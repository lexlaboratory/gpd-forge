# GPD Forge — SPIKE: elevated EC fan-RPM probe via the PawnIO LibreHardwareMonitor build. GPL-3.0-or-later.
# Self-elevates (accept UAC), runs the service's read-only --probe-ec, writes the result to a file.
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Host "Elevation required for EC/PawnIO access - relaunching (accept UAC)..." -ForegroundColor Yellow
  Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
  return
}
$env:Path = [System.Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path', 'User')
$repo = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $repo 'core\bin\Release\net9.0-windows\GpdForge.Service.dll'
$out = Join-Path $repo 'dist-release\spike-probe-ec.txt'
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
if (-not (Test-Path $dll)) { "BUILD MISSING: $dll" | Set-Content $out; return }
"=== $(Get-Date -Format o) probe-ec (PawnIO LHM 0.9.7-pre726) ===" | Set-Content $out
& dotnet $dll --probe-ec *>> $out
"exit: $LASTEXITCODE" | Add-Content $out
Write-Host "Done. Result written to $out" -ForegroundColor Green
