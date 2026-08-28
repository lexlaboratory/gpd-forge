<#
  GPD Forge - installer. GPL-3.0-or-later.

  Installs GPD Forge and sets it to run automatically, in a way that works under Smart App Control
  (no unsigned executable is launched):
    - publishes the core service and registers it as a Windows Service run via the SIGNED dotnet.exe
      host (SYSTEM, autostart), so the local API + real telemetry come up at boot,
    - builds the web UI AND the native Tauri shell from source (never copies a stale binary),
      opening a native 1024x720 window,
    - starts the service and runs scripts\verify-install.ps1, failing loudly if anything is off.

  It does NOT change power or fan on its own, and does NOT touch MotionAssistant / GPD Tool unless
  you pass -Substitute.

  Usage (run from the repo root; it self-elevates):
    powershell -ExecutionPolicy Bypass -File scripts\install-gpd-forge.ps1
    ...\install-gpd-forge.ps1 -Substitute     # also stop+disable MotionAssistant & GPD Tool (the takeover)
    ...\install-gpd-forge.ps1 -NoHardware     # telemetry in driverless WMI mode only
    ...\install-gpd-forge.ps1 -NoFps          # skip the PresentMon/ETW FPS probe (fps stays 0)
    ...\install-gpd-forge.ps1 -NoFanControl   # telemetry + TDP only; leave the fan to the EC
    ...\install-gpd-forge.ps1 -Uninstall      # remove the service and shortcut
#>
[CmdletBinding()]
param(
    [switch]$Substitute,
    [switch]$Uninstall,
    [switch]$NoHardware,
    [switch]$NoFps,
    [switch]$NoFanControl
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'GPDForge'
$InstallDir  = 'C:\Program Files\GPD Forge'
$RepoDir     = Split-Path $PSScriptRoot -Parent
$StartMenu   = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
$StartupDir  = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
$Url         = 'http://127.0.0.1:8787'

# --- self-elevate ---
$LogPath = Join-Path $RepoDir 'scripts\logs\install-gpd-forge.log'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevation required - relaunching (accept UAC)..." -ForegroundColor Yellow
    Write-Host "  the elevated half logs to $LogPath" -ForegroundColor DarkGray
    $fwd = @()
    foreach ($kv in $PSBoundParameters.GetEnumerator()) { if ($kv.Value -is [switch] -and $kv.Value.IsPresent) { $fwd += "-$($kv.Key)" } }
    Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList (@('-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"") + $fwd)
    return
}
# The elevated half runs hidden, so without a transcript a failure here is invisible: the script
# just stops and leaves the machine with no service. Everything past this point is recorded.
New-Item -ItemType Directory -Force -Path (Split-Path $LogPath -Parent) | Out-Null
try { Start-Transcript -Path $LogPath -Force | Out-Null } catch { }
trap { Write-Host "UNHANDLED: $_" -ForegroundColor Red; try { Stop-Transcript | Out-Null } catch { }; break }
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
Write-Host "== 1/7  Publishing the core service ==" -ForegroundColor Cyan
Remove-ForgeService
New-Item -ItemType Directory -Force -Path "$InstallDir\service" | Out-Null
dotnet publish "$RepoDir\core\GpdForge.Service.csproj" -c Release -o "$InstallDir\service" --nologo
$dll = "$InstallDir\service\GpdForge.Service.dll"
if (-not (Test-Path $dll)) { Write-Host "Publish failed (need the .NET 9 SDK)." -ForegroundColor Red; return }

# --- 2) build the web UI and let the service serve it (wwwroot) ---
# The bundle is origin-agnostic on purpose: ui/src/api.ts detects the Tauri shell at runtime
# (origin http://tauri.localhost) and targets the daemon absolutely, while the browser served
# from wwwroot stays same-origin. Do NOT bake VITE_FORGE_API in - one bundle serves both.
Write-Host "== 2/7  Building the web UI ==" -ForegroundColor Cyan
$env:VITE_FORGE_API = ''
npm --prefix "$RepoDir\ui" run build --silent
if (-not (Test-Path "$RepoDir\ui\dist\index.html")) { Write-Host "UI build failed (no dist\index.html)." -ForegroundColor Red; return }
# wipe wwwroot first: Copy-Item -Force overwrites but never deletes, so stale bundles from every
# previous install pile up there forever and it stops being obvious which one is live.
if (Test-Path "$InstallDir\service\wwwroot") { Remove-Item -Recurse -Force "$InstallDir\service\wwwroot" }
New-Item -ItemType Directory -Force -Path "$InstallDir\service\wwwroot" | Out-Null
Copy-Item "$RepoDir\ui\dist\*" "$InstallDir\service\wwwroot\" -Recurse -Force
Copy-Item "$RepoDir\ui\src-tauri\icons\icon.ico" "$InstallDir\icon.ico" -Force
Copy-Item "$RepoDir\scripts\forge-notify.ps1" "$InstallDir\forge-notify.ps1" -Force

# --- 2b) build the native Tauri shell, then install it ---
# This step used to be a bare Copy-Item of whatever happened to sit in target\release. That is how
# a shell binary older than the UI it was supposed to embed reached Program Files on 2026-08-27 and
# left the dashboard showing "--" for every tile. Build it, and refuse to install without it.
Write-Host "== 3/7  Building the native desktop shell ==" -ForegroundColor Cyan
$tauriExe = "$RepoDir\ui\src-tauri\target\release\gpd-forge.exe"
npm --prefix "$RepoDir\ui" run tauri build
if (-not (Test-Path $tauriExe)) {
    Write-Host "Tauri shell build failed - no gpd-forge.exe produced (need the Rust toolchain)." -ForegroundColor Red
    Write-Host "Refusing to install: copying a stale shell is what broke telemetry last time." -ForegroundColor Red
    return
}
if ((Get-Item $tauriExe).LastWriteTime -lt (Get-Item "$RepoDir\ui\dist\index.html").LastWriteTime) {
    Write-Host "Tauri shell is older than the UI bundle it should embed - build did not run." -ForegroundColor Red
    return
}
# The shell being replaced is very often running - the previous install opened it, or it is sitting
# in the tray. Windows keeps a lock on a running image, so Copy-Item fails; before the transcript
# existed that failure was invisible and left the machine with no service at all.
foreach ($name in @('GPD Forge', 'gpd-forge')) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  stopping running shell (pid $($_.Id))" -ForegroundColor DarkGray
        try { $_.Kill() } catch { }
    }
}
Start-Sleep -Milliseconds 500
Copy-Item $tauriExe "$InstallDir\GPD Forge.exe" -Force

# --- 4) register the service via the signed dotnet host (SAC-safe) ---
Write-Host "== 4/7  Registering the Windows Service ==" -ForegroundColor Cyan
$dotnet = (Get-Command dotnet).Source
New-Service -Name $ServiceName -BinaryPathName "`"$dotnet`" `"$dll`"" -DisplayName "GPD Forge" `
    -Description "GPD Forge - handheld tuning daemon (local API + telemetry + web UI)." -StartupType Automatic | Out-Null
$envLines = @("GPDFORGE_AUTO_PROFILES=1")
if (-not $NoHardware) { $envLines += "GPDFORGE_ENABLE_HARDWARE=1" }
# Fan WRITES need a SECOND opt-in on top of the hardware gate (see core/Fan/FanControlPolicy.cs):
# commanding the wrong duty is an immediate physical risk, so it is deliberately not implied by
# -NoHardware alone. The installer used to omit it entirely, which meant a reinstall silently
# CLOSED a gate an operator had opened by hand and left every fan control inert.
if (-not $NoHardware -and -not $NoFanControl) { $envLines += "GPDFORGE_ENABLE_FAN_CONTROL=1" }
# FPS lives behind its own gate: it is an ETW capability, unrelated to the MSR/EC access above, and a
# failure there must not be able to take the (physically validated) hardware path down with it.
if (-not $NoFps) {
    # PresentMon sits next to the service DLL, where PresentMonFrameRateProbe.Locate() looks.
    # If the fetch fails (no network, signature mismatch) the gate still opens but the probe finds
    # nothing and fps stays 0 - an optional sensor must never block the install.
    & "$RepoDir\scripts\fetch-presentmon.ps1"
    $pmSrc = "$RepoDir\vendor\presentmon\PresentMon.exe"
    if (Test-Path $pmSrc) {
        New-Item -ItemType Directory -Force -Path "$InstallDir\service\presentmon" | Out-Null
        Copy-Item $pmSrc "$InstallDir\service\presentmon\PresentMon.exe" -Force
        $envLines += "GPDFORGE_ENABLE_FPS=1"
    } else {
        Write-Host "  PresentMon unavailable - FPS telemetry stays off (fps will read 0)." -ForegroundColor Yellow
    }
}
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment `
    -PropertyType MultiString -Value $envLines -Force | Out-Null

# --- 5) Start Menu + session tray shortcuts (signed hosts) ---
Write-Host "== 5/7  Creating Start Menu and tray shortcuts ==" -ForegroundColor Cyan
if (Test-Path "$StartMenu\GPD Forge.url") { Remove-Item -Force "$StartMenu\GPD Forge.url" }
$wsh = New-Object -ComObject WScript.Shell
$startLink = $wsh.CreateShortcut("$StartMenu\GPD Forge.lnk")
$startLink.TargetPath = "$InstallDir\GPD Forge.exe"; $startLink.Arguments = ''; $startLink.WorkingDirectory = $InstallDir
$startLink.IconLocation = "$InstallDir\icon.ico"; $startLink.Description = 'Open GPD Forge dashboard'; $startLink.Save()
New-Item -ItemType Directory -Force -Path $StartupDir | Out-Null
$trayLink = $wsh.CreateShortcut("$StartupDir\GPD Forge Tray.lnk")
$trayLink.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$trayLink.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$InstallDir\forge-notify.ps1`""
$trayLink.WorkingDirectory = $InstallDir; $trayLink.IconLocation = "$InstallDir\icon.ico"; $trayLink.Description = 'GPD Forge premium tray icon'; $trayLink.Save()

# --- 6) start the service ---
Write-Host "== 6/7  Starting the service ==" -ForegroundColor Cyan
Start-Service $ServiceName
Start-Sleep -Seconds 3

# --- optional: substitute MotionAssistant / GPD Tool ---
if ($Substitute) {
    Write-Host "== 6b  Substituting MotionAssistant / GPD Tool ==" -ForegroundColor Cyan
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

# --- 7) verify the install end to end, then open ---
# A green install that ships a dashboard showing "--" is worse than a red one. verify-install.ps1
# checks the things the test suite structurally cannot: that the shell binary in Program Files is
# the one we just built, and that it can actually reach the daemon.
Write-Host "== 7/7  Verifying the installation ==" -ForegroundColor Cyan
& "$RepoDir\scripts\verify-install.ps1"
$verifyOk = ($LASTEXITCODE -eq 0)

if ($verifyOk) {
    Start-Process "$InstallDir\GPD Forge.exe"
    Write-Host "`nDone. GPD Forge runs as a service (autostart). Open the dashboard from the Start Menu" -ForegroundColor Green
    Write-Host "('GPD Forge') or from the premium tray icon. The local API remains at $Url." -ForegroundColor DarkGray
} else {
    Write-Host "`nInstall completed but verification FAILED - see the checks above." -ForegroundColor Red
    Write-Host "Not opening the window: it would most likely show '--' for every tile." -ForegroundColor Red
    exit 1
}
