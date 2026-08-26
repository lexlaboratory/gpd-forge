# GPD Forge - portable bundle packager (Via 0, Smart App Control-safe). GPL-3.0-or-later.
#
# Produces a zip that runs WITHOUT installing a service and WITHOUT any unsigned binary of ours:
# the service is published framework-dependent with NO apphost (-p:UseAppHost=false), so the bundle
# ships only managed DLLs + wwwroot, and a .cmd launcher starts it via the signed `dotnet` host and
# opens the dashboard in the (signed) browser. Requires the .NET 9 runtime on the target machine.
#
#   powershell -ExecutionPolicy Bypass -File scripts\package-portable.ps1 [-Version 0.1.0]
param(
  [string]$Version = '0.1.0',
  [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist-release')
)
$ErrorActionPreference = 'Stop'
$env:Path = [System.Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path', 'User')
$Repo = Split-Path $PSScriptRoot -Parent
$Stage = Join-Path $OutDir "GPDForge-portable-v$Version"
$Svc = Join-Path $Stage 'service'

Write-Host "== Publishing the service (framework-dependent, no apphost) ==" -ForegroundColor Cyan
if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
New-Item -ItemType Directory -Force -Path $Svc | Out-Null
dotnet publish "$Repo\core\GpdForge.Service.csproj" -c Release -o $Svc --nologo -p:UseAppHost=false
$dll = Join-Path $Svc 'GpdForge.Service.dll'
if (-not (Test-Path $dll)) { throw 'Publish failed (need the .NET 9 SDK).' }
# Belt-and-suspenders: no unsigned native launcher should ship in the bundle.
Get-ChildItem $Svc -Filter 'GpdForge.Service.exe' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "== Building the web UI (same-origin) ==" -ForegroundColor Cyan
$env:VITE_FORGE_API = ''
npm --prefix "$Repo\ui" run build --silent
New-Item -ItemType Directory -Force -Path (Join-Path $Svc 'wwwroot') | Out-Null
Copy-Item "$Repo\ui\dist\*" (Join-Path $Svc 'wwwroot') -Recurse -Force

Write-Host "== Writing launcher + readme ==" -ForegroundColor Cyan
$launcher = @'
@echo off
rem GPD Forge portable launcher - starts the daemon via the signed dotnet host and opens the dashboard.
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo The .NET 9 runtime is required: https://dotnet.microsoft.com/download & pause & exit /b 1)
set GPDFORGE_AUTO_PROFILES=1
echo Starting GPD Forge on http://127.0.0.1:8787  (close this window to stop)
start "" "http://127.0.0.1:8787"
dotnet "%~dp0service\GpdForge.Service.dll"
'@
Set-Content -Path (Join-Path $Stage 'GPD Forge.cmd') -Value $launcher -Encoding ascii

$readme = @"
GPD Forge $Version - portable

Runs without installing anything and without any unsigned executable (so it is fine under
Smart App Control): the daemon is started by the signed Microsoft dotnet host and the dashboard
opens in your browser.

REQUIREMENT: the .NET 9 Desktop Runtime (https://dotnet.microsoft.com/download/dotnet/9.0).

RUN:  double-click "GPD Forge.cmd"  ->  the dashboard opens at http://127.0.0.1:8787
STOP: close the console window it opened.

Notes
- Driverless WMI telemetry by default. For package watts / temps, set GPDFORGE_ENABLE_HARDWARE=1
  and run the .cmd elevated.
- For autostart-at-boot as a Windows service instead, use scripts\install-gpd-forge.ps1 from the repo.
- The native desktop app (.exe) needs code-signing to run under Smart App Control - see docs/signing.md.

GPL-3.0-or-later - https://github.com/lexlaboratory/gpd-forge
"@
Set-Content -Path (Join-Path $Stage 'README.txt') -Value $readme -Encoding ascii

Write-Host "== Zipping ==" -ForegroundColor Cyan
$zip = Join-Path $OutDir "GPDForge-portable-v$Version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $Stage -DestinationPath $zip -CompressionLevel Optimal
$size = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host ("Portable bundle: $zip  ($size MB)") -ForegroundColor Green
Write-Output $zip
