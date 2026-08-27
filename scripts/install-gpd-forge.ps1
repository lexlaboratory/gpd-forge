<#
  GPD Forge - installer. GPL-3.0-or-later.

  Installs GPD Forge and sets it to run automatically, in a way that works under Smart App Control
  (no unsigned executable is launched):
    - publishes the core service and registers it as a Windows Service run via the SIGNED dotnet.exe
      host (SYSTEM, autostart), so the local API + real telemetry come up at boot,
    - builds the web UI and lets the service serve it, so you open the dashboard in your BROWSER at
      http://127.0.0.1:8787 (a signed app) instead of an unsigned desktop binary,
    - starts the service and verifies the API.

  It does NOT change power or fan on its own, and does NOT touch MotionAssistant / GPD Tool unless
  you pass -Substitute.

  Usage (run from the repo root; it self-elevates):
    powershell -ExecutionPolicy Bypass -File scripts\install-gpd-forge.ps1
    ...\install-gpd-forge.ps1 -Substitute     # also stop+disable MotionAssistant & GPD Tool (the takeover)
    ...\install-gpd-forge.ps1 -NoHardware     # telemetry in driverless WMI mode only
    ...\install-gpd-forge.ps1 -Uninstall      # remove the service and shortcut
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
$StartMenu   = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
$StartupDir  = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
$Url         = 'http://127.0.0.1:8787'

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

function Remove-ForgeService {
    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue }
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 1
    }
}

if ($Uninstall) {
    Remove-ForgeService
    if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
    if (Test-Path "$StartMenu\GPD Forge.url") { Remove-Item -Force "$StartMenu\GPD Forge.url" }
    foreach ($p in @("$StartMenu\GPD Forge.lnk", "$StartupDir\GPD Forge Tray.lnk")) { if (Test-Path $p) { Remove-Item -Force $p } }
    Write-Host "GPD Forge removed." -ForegroundColor Green
    return
}

# --- 1) publish the service ---
Write-Host "== 1/6  Publishing the core service ==" -ForegroundColor Cyan
Remove-ForgeService
New-Item -ItemType Directory -Force -Path "$InstallDir\service" | Out-Null
dotnet publish "$RepoDir\core\GpdForge.Service.csproj" -c Release -o "$InstallDir\service" --nologo
$dll = "$InstallDir\service\GpdForge.Service.dll"
if (-not (Test-Path $dll)) { Write-Host "Publish failed (need the .NET 9 SDK)." -ForegroundColor Red; return }

# --- 2) build the web UI and let the service serve it (wwwroot) ---
Write-Host "== 2/6  Building the web UI ==" -ForegroundColor Cyan
$env:VITE_FORGE_API = ''   # same-origin: the service serves the UI and the API together
npm --prefix "$RepoDir\ui" run build --silent
New-Item -ItemType Directory -Force -Path "$InstallDir\service\wwwroot" | Out-Null
Copy-Item "$RepoDir\ui\dist\*" "$InstallDir\service\wwwroot\" -Recurse -Force
Copy-Item "$RepoDir\ui\src-tauri\icons\icon.ico" "$InstallDir\icon.ico" -Force
Copy-Item "$RepoDir\scripts\forge-notify.ps1" "$InstallDir\forge-notify.ps1" -Force

# --- 3) register the service via the signed dotnet host (SAC-safe) ---
Write-Host "== 3/6  Registering the Windows Service ==" -ForegroundColor Cyan
$dotnet = (Get-Command dotnet).Source
New-Service -Name $ServiceName -BinaryPathName "`"$dotnet`" `"$dll`"" -DisplayName "GPD Forge" `
    -Description "GPD Forge - handheld tuning daemon (local API + telemetry + web UI)." -StartupType Automatic | Out-Null
$envLines = @("GPDFORGE_AUTO_PROFILES=1")
if (-not $NoHardware) { $envLines += "GPDFORGE_ENABLE_HARDWARE=1" }
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment `
    -PropertyType MultiString -Value $envLines -Force | Out-Null

# --- 4) Start Menu + session tray shortcuts (signed hosts) ---
Write-Host "== 4/6  Creating Start Menu and tray shortcuts ==" -ForegroundColor Cyan
if (Test-Path "$StartMenu\GPD Forge.url") { Remove-Item -Force "$StartMenu\GPD Forge.url" }
$wsh = New-Object -ComObject WScript.Shell
$startLink = $wsh.CreateShortcut("$StartMenu\GPD Forge.lnk")
$startLink.TargetPath = "$env:WINDIR\explorer.exe"; $startLink.Arguments = $Url; $startLink.WorkingDirectory = $InstallDir
$startLink.IconLocation = "$InstallDir\icon.ico"; $startLink.Description = 'Open GPD Forge dashboard'; $startLink.Save()
New-Item -ItemType Directory -Force -Path $StartupDir | Out-Null
$trayLink = $wsh.CreateShortcut("$StartupDir\GPD Forge Tray.lnk")
$trayLink.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$trayLink.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$InstallDir\forge-notify.ps1`""
$trayLink.WorkingDirectory = $InstallDir; $trayLink.IconLocation = "$InstallDir\icon.ico"; $trayLink.Description = 'GPD Forge premium tray icon'; $trayLink.Save()

# --- 5) start the service ---
Write-Host "== 5/6  Starting the service ==" -ForegroundColor Cyan
Start-Service $ServiceName
Start-Sleep -Seconds 3

# --- optional: substitute MotionAssistant / GPD Tool ---
if ($Substitute) {
    Write-Host "== 5b  Substituting MotionAssistant / GPD Tool ==" -ForegroundColor Cyan
    Write-Host "  (two power controllers must not run together - stopping + disabling the incumbents)" -ForegroundColor Yellow
    foreach ($p in @('MotionAssistant','pmgui','GPDTool','GPDKeyboard')) {
        Get-Process $p -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    $gsvc = Get-Service 'GPDToolService' -ErrorAction SilentlyContinue
    if ($gsvc) { Stop-Service 'GPDToolService' -Force -ErrorAction SilentlyContinue; Set-Service 'GPDToolService' -StartupType Disabled }
    # disable their autostart (Run keys), REVERSIBLY (renamed, not deleted)
    foreach ($hive in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\Run')) {
        $props = Get-ItemProperty $hive -ErrorAction SilentlyContinue
        if ($props) {
            $props.PSObject.Properties | Where-Object { $_.Value -match 'GPD\\GPDTool|Motion Assistant|MotionAssistant' } | ForEach-Object {
                try { Rename-ItemProperty -Path $hive -Name $_.Name -NewName ($_.Name + '_disabledByGPDForge') -ErrorAction Stop; Write-Host "    autostart disabled: $($_.Name)" } catch {}
            }
        }
    }
    # scheduled tasks (best-effort; ignore if they do not exist)
    foreach ($t in @('MotionAssistant','GPDTool')) { try { & schtasks /Change /TN $t /DISABLE *> $null } catch {} }
    Write-Host "  incumbents stopped, service + autostart disabled." -ForegroundColor Green
}

# --- 6) verify + open ---
Write-Host "== 6/6  Verifying the local API ==" -ForegroundColor Cyan
try {
    $h = Invoke-RestMethod "$Url/health" -TimeoutSec 8
    Write-Host ("  API up: " + $h.model) -ForegroundColor Green
    $t = Invoke-RestMethod "$Url/telemetry" -TimeoutSec 8
    Write-Host ("  telemetry: cpu=" + $t.cpuTempC + "C  packageW=" + $t.packageW + "  battery=" + $t.batteryPct + "%")
    Start-Process $Url    # open the dashboard in the default (signed) browser
} catch {
    Write-Host ("  API not answering yet: " + $_.Exception.Message) -ForegroundColor Yellow
    Write-Host ("  check the service: Get-Service $ServiceName ; and the log with 'sc query $ServiceName'") -ForegroundColor DarkGray
}

Write-Host "`nDone. GPD Forge runs as a service (autostart). Open the dashboard from the Start Menu" -ForegroundColor Green
Write-Host "('GPD Forge') or at $Url. The native desktop app needs code-signing to run under Smart App Control." -ForegroundColor DarkGray
