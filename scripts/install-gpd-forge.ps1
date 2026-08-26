<#
  GPD Forge - installer. GPL-3.0-or-later.

  Installs GPD Forge and sets it to run automatically:
    - publishes the core service and registers it as a Windows Service (SYSTEM, autostart) so the
      local API + real telemetry come up at boot,
    - installs the desktop app (runs the built NSIS setup if present, else drops a shortcut to the exe),
    - starts the service and verifies the API.

  It does NOT change power or fan on its own (TDP writes still require an explicit action), and it does
  NOT touch MotionAssistant / GPD Tool unless you pass -Substitute.

  Usage (run from the repo root; it self-elevates):
    powershell -ExecutionPolicy Bypass -File scripts\install-gpd-forge.ps1
    ...\install-gpd-forge.ps1 -Substitute     # also stop+disable MotionAssistant & GPD Tool (the takeover)
    ...\install-gpd-forge.ps1 -Uninstall      # remove the service and app
    ...\install-gpd-forge.ps1 -NoHardware     # install telemetry in driverless WMI mode only
#>
[CmdletBinding()]
param(
    [switch]$Substitute,
    [switch]$Uninstall,
    [switch]$NoHardware
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'GPDForge'
$InstallDir  = 'C:\Program Files\GPD Forge'
$RepoDir     = Split-Path $PSScriptRoot -Parent

# --- self-elevate ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevation required - relaunching (accept UAC)..." -ForegroundColor Yellow
    $fwd = @()
    foreach ($kv in $PSBoundParameters.GetEnumerator()) { if ($kv.Value -is [switch] -and $kv.Value.IsPresent) { $fwd += "-$($kv.Key)" } }
    Start-Process powershell -Verb RunAs -ArgumentList (@('-NoExit','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"") + $fwd)
    return
}
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")

function Stop-And-Remove-Service {
    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Host "Stopping + removing existing service..." -ForegroundColor Cyan
        if ($svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue }
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 1
    }
}

if ($Uninstall) {
    Stop-And-Remove-Service
    if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
    $lnk = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\GPD Forge.lnk"
    if (Test-Path $lnk) { Remove-Item -Force $lnk }
    Write-Host "GPD Forge removed." -ForegroundColor Green
    return
}

# --- 1) publish the service ---
Write-Host "== 1/5  Publishing the core service ==" -ForegroundColor Cyan
Stop-And-Remove-Service
New-Item -ItemType Directory -Force -Path "$InstallDir\service" | Out-Null
dotnet publish "$RepoDir\core\GpdForge.Service.csproj" -c Release -o "$InstallDir\service" --nologo
$svcExe = "$InstallDir\service\GpdForge.Service.exe"
if (-not (Test-Path $svcExe)) { Write-Host "Publish failed (need the .NET 9 SDK)." -ForegroundColor Red; return }

# --- 2) register the Windows Service (SYSTEM, autostart) ---
Write-Host "== 2/5  Registering the Windows Service ==" -ForegroundColor Cyan
New-Service -Name $ServiceName -BinaryPathName "`"$svcExe`"" -DisplayName "GPD Forge" -Description "GPD Forge - handheld tuning daemon (local API + telemetry)." -StartupType Automatic | Out-Null
# service-scoped environment (hardware read-only sensors ON unless -NoHardware; auto-profiles ON)
$envLines = @("GPDFORGE_AUTO_PROFILES=1")
if (-not $NoHardware) { $envLines += "GPDFORGE_ENABLE_HARDWARE=1" }
$regKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $regKey -Name Environment -PropertyType MultiString -Value $envLines -Force | Out-Null

# --- 3) install the desktop app ---
Write-Host "== 3/5  Installing the desktop app ==" -ForegroundColor Cyan
$setup = Get-ChildItem -Recurse "$RepoDir\ui\src-tauri\target\release\bundle" -Filter *setup*.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($setup) {
    Write-Host "  running installer: $($setup.Name)"
    Start-Process $setup.FullName -ArgumentList "/S" -Wait
} else {
    $appExe = "$RepoDir\ui\src-tauri\target\release\gpd-forge.exe"
    if (Test-Path $appExe) {
        Copy-Item $appExe "$InstallDir\gpd-forge.exe" -Force
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\GPD Forge.lnk")
        $sc.TargetPath = "$InstallDir\gpd-forge.exe"; $sc.Save()
        Write-Host "  installer not built yet; placed exe + Start Menu shortcut."
    } else {
        Write-Host "  desktop app not built (run: npx tauri build in ui\). Service still installs." -ForegroundColor Yellow
    }
}

# --- 4) start the service ---
Write-Host "== 4/5  Starting the service ==" -ForegroundColor Cyan
Start-Service $ServiceName
Start-Sleep -Seconds 2

# --- optional: substitute MotionAssistant / GPD Tool ---
if ($Substitute) {
    Write-Host "== 4b  Substituting MotionAssistant / GPD Tool ==" -ForegroundColor Cyan
    Write-Host "  (two power controllers must not run together - stopping + disabling the incumbents)" -ForegroundColor Yellow
    foreach ($p in @('MotionAssistant','pmgui','GPDTool','GPDKeyboard')) {
        Get-Process $p -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    foreach ($s in @('GPDToolService')) {
        $svc = Get-Service $s -ErrorAction SilentlyContinue
        if ($svc) { Stop-Service $s -Force -ErrorAction SilentlyContinue; Set-Service $s -StartupType Disabled }
    }
    foreach ($t in @('MotionAssistant','GPDTool')) {
        schtasks /Change /TN $t /DISABLE 2>$null | Out-Null
    }
    Write-Host "  incumbents stopped/disabled. (Re-enable them with GPD's own tools if you revert.)" -ForegroundColor Green
}

# --- 5) verify ---
Write-Host "== 5/5  Verifying the local API ==" -ForegroundColor Cyan
try {
    $h = Invoke-RestMethod "http://127.0.0.1:8787/health" -TimeoutSec 5
    Write-Host ("  API up: " + $h.model) -ForegroundColor Green
    $t = Invoke-RestMethod "http://127.0.0.1:8787/telemetry" -TimeoutSec 5
    Write-Host ("  telemetry: cpu=" + $t.cpuTempC + "C  packageW=" + $t.packageW + "  battery=" + $t.batteryPct + "%")
} catch {
    Write-Host ("  API not answering yet: " + $_.Exception.Message) -ForegroundColor Yellow
}

Write-Host "`nDone. GPD Forge service is installed and set to start automatically." -ForegroundColor Green
Write-Host "Open the GPD Forge app from the Start Menu. TDP/fan writes remain gated until you enable them." -ForegroundColor DarkGray
