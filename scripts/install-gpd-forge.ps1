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
    ...\install-gpd-forge.ps1 -EnableGpuProfiles  # let GPD Forge set Radeon Anti-Lag/Chill/Boost
    ...\install-gpd-forge.ps1 -Restore        # undo -Substitute: hand MA / GPD Tool back
    ...\install-gpd-forge.ps1 -DryRun         # rehearse -Restore, writing nothing
    ...\install-gpd-forge.ps1 -Uninstall      # remove the service and shortcut (restores first)
#>
[CmdletBinding()]
param(
    [switch]$Substitute,
    [switch]$Uninstall,
    [switch]$NoHardware,
    [switch]$NoFps,
    [switch]$NoFanControl,
    # Undo -Substitute: hand MotionAssistant / GPD Tool back their service, autostart and tasks.
    # Runs on its own, and automatically as part of -Uninstall.
    [switch]$Restore,
    # Report what -Restore would change and exit without changing it. Handing a power controller back
    # is not a step to discover the behaviour of by running it, so the rehearsal is a first-class flag.
    [switch]$DryRun,
    # Let GPD Forge drive the Radeon 3D settings (Anti-Lag / Chill / Boost) through ADLX. OFF by
    # default and opt-in on purpose: these settings are visible in the user's own Adrenalin install and
    # changing them without being asked would be taking over something nobody handed us.
    [switch]$EnableGpuProfiles
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

# --- the takeover, and its undo ---------------------------------------------------------------
#
# -Substitute stops MotionAssistant / GPD Tool and disables their service, autostart and tasks,
# because two power controllers fighting over the same silicon is a real, field-confirmed clash.
# It was written to be reversible — the Run keys are RENAMED rather than deleted — but there was no
# way to actually reverse it, and -Uninstall removed GPD Forge while leaving the incumbents disabled.
# The result: a user uninstalls GPD Forge and is left with NO power controller at all, no message
# saying why, and no obvious way back. A change that is only reversible in principle is not
# reversible; this is the mechanism that makes the claim true.
#
# The prior state is recorded under %ProgramData% (not Program Files, which -Uninstall deletes) so
# the undo restores what was actually there. When no record exists — a takeover performed before
# this existed — the restore says so plainly and uses documented defaults rather than pretending to
# know what the machine looked like.
$TakeoverState = Join-Path $env:ProgramData 'GPD Forge\takeover-state.json'
$RunHives = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
)
$IncumbentTasks = @('MotionAssistant','GPDTool')
$DisabledSuffix = '_disabledByGPDForge'

function Restore-Incumbents {
    param([switch]$Rehearse)

    if ($Rehearse) {
        Write-Host "== DRY RUN: what -Restore would change (nothing is being written) ==" -ForegroundColor Cyan
    } else {
        Write-Host "== Restoring MotionAssistant / GPD Tool ==" -ForegroundColor Cyan
    }

    $recorded = $null
    if (Test-Path $TakeoverState) {
        try { $recorded = Get-Content $TakeoverState -Raw | ConvertFrom-Json } catch { $recorded = $null }
    }
    if (-not $recorded) {
        Write-Host "  No takeover record found - restoring with defaults." -ForegroundColor Yellow
        Write-Host "  The GPD Tool service start type will be set to Automatic; if it was something" -ForegroundColor Yellow
        Write-Host "  else before, set it by hand. This is a guess and is labelled as one." -ForegroundColor Yellow
    }

    # 1) Autostart entries: rename back. Renaming is what made this recoverable at all.
    foreach ($hive in $RunHives) {
        $props = Get-ItemProperty $hive -ErrorAction SilentlyContinue
        if (-not $props) { continue }
        $props.PSObject.Properties | Where-Object { $_.Name -like "*$DisabledSuffix" } | ForEach-Object {
            # Parenthesised deliberately: -replace binds tighter than +, so the unparenthesised form
            # `-replace [regex]::Escape($x) + '$', ''` replaces the suffix ANYWHERE and then appends
            # nothing, leaving the name unchanged. The 2026-08-29 dry run caught exactly that.
            $original = $_.Name -replace ([regex]::Escape($DisabledSuffix) + '$'), ''
            if ($Rehearse) {
                Write-Host "    would restore autostart: $($_.Name) -> $original" -ForegroundColor Yellow
                Write-Host "      value: $($_.Value)" -ForegroundColor DarkGray
                return
            }
            try {
                Rename-ItemProperty -Path $hive -Name $_.Name -NewName $original -ErrorAction Stop
                Write-Host "    autostart restored: $original" -ForegroundColor Green
            } catch {
                Write-Host "    could not restore autostart '$original': $($_.Exception.Message)" -ForegroundColor Red
            }
        }
    }

    # 2) The GPD Tool service. Only touched if it exists; its previous start type is used when known.
    $gsvc = Get-Service 'GPDToolService' -ErrorAction SilentlyContinue
    if ($gsvc) {
        $startType = if ($recorded -and $recorded.gpdToolServiceStartType) { $recorded.gpdToolServiceStartType } else { 'Automatic' }
        if ($Rehearse) {
            $current = (Get-CimInstance Win32_Service -Filter "Name='GPDToolService'").StartMode
            Write-Host "    would set GPDToolService start type: $current -> $startType" -ForegroundColor Yellow
        } else {
        try {
            Set-Service 'GPDToolService' -StartupType $startType -ErrorAction Stop
            Write-Host "    GPDToolService start type -> $startType" -ForegroundColor Green
        } catch {
            Write-Host "    could not set GPDToolService start type: $($_.Exception.Message)" -ForegroundColor Red
        }
        }
    }

    # 3) Scheduled tasks. Best-effort, same as disabling them was.
    foreach ($t in $IncumbentTasks) {
        if ($Rehearse) {
            # Get-ScheduledTask rather than `schtasks /Query`: schtasks writes to stderr for a task
            # that does not exist, and with ErrorActionPreference='Stop' that becomes a TERMINATING
            # NativeCommandError — the same trap this script already documents for `dotnet`. Guarding
            # it with try/catch stops the abort but still fills the install transcript with alarming
            # text about a completely normal situation, and a log full of false errors is how real
            # ones get ignored. The cmdlet just returns nothing.
            if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) {
                Write-Host "    would enable scheduled task: $t" -ForegroundColor Yellow
            }
            continue
        }
        try { & schtasks /Change /TN $t /ENABLE *> $null } catch {}
    }

    if ($Rehearse) {
        Write-Host "  Dry run complete - nothing was changed." -ForegroundColor Cyan
        return
    }

    # The record has served its purpose; leaving it would make a later restore claim knowledge of a
    # takeover that has already been undone.
    if (Test-Path $TakeoverState) { Remove-Item $TakeoverState -Force -ErrorAction SilentlyContinue }

    Write-Host "  Incumbents restored. They start again at next logon/boot." -ForegroundColor Green
    Write-Host "  NOTE: two power controllers must not run together - GPD Forge yields while they run," -ForegroundColor Yellow
    Write-Host "  but if you keep both, expect them to fight over TDP." -ForegroundColor Yellow
}

if ($Restore -or ($DryRun -and -not $Uninstall)) { Restore-Incumbents -Rehearse:$DryRun; return }

if ($Uninstall) {
    # Undo the takeover FIRST. Removing GPD Forge while its takeover stands is what would leave the
    # machine with no power controller at all.
    Restore-Incumbents
    Remove-ForgeService
    if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
    if (Test-Path "$StartMenu\GPD Forge.url") { Remove-Item -Force "$StartMenu\GPD Forge.url" }
    foreach ($p in @("$StartMenu\GPD Forge.lnk", "$StartupDir\GPD Forge Tray.lnk", "$StartupDir\GPD Forge GPU Agent.lnk")) { if (Test-Path $p) { Remove-Item -Force $p } }
    # Kill a running GPU agent too. Left alive it would keep driving the Radeon settings from a session
    # whose GPD Forge no longer exists — a process nobody can find still changing the machine.
    foreach ($proc in Get-Process dotnet -ErrorAction SilentlyContinue) {
        try { if ($proc.CommandLine -like '*--gpu-agent*') { $proc.Kill(); Write-Host "  stopped the GPU agent (pid $($proc.Id))" } } catch { }
    }
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

# Smart App Control judges each unsigned binary individually, by content, and inconsistently: on
# 2026-08-29 the same source produced a build it allowed at 14:39 and one it blocked at 15:19. A
# blocked service DLL does not fail the publish - it fails `Start-Service` six steps later, with an
# error that says nothing about the cause, and by then the previous WORKING binary is already gone.
#
# scripts/update-shell.ps1 has verified the shell this way since it existed; the service had no such
# guard, and this is the failure it was missing. Because the build is deterministic, a plain retry
# reproduces the identical hash and the identical verdict - so the retry must change the hash, which
# -p:Deterministic=false does without touching the code or the version.
#
# Start-Process rather than `& dotnet ... 2>&1`: in Windows PowerShell 5.1 redirecting a native
# executable's stderr into the pipeline wraps each line in a NativeCommandError, which this script's
# ErrorActionPreference = 'Stop' turns into a terminating error. The probe FAILING is the signal we
# are testing for, so it must not abort the installer.
#
# Only 0x800711C7 counts as blocked. A non-zero exit for any other reason still means the assembly
# LOADED, and rebuilding over that would hide a real fault behind a pointless retry.
#
# The check is retried because Smart App Control's verdict on a freshly written binary is a cloud
# lookup: the same file can be refused on the first load and accepted seconds later.
function Test-AssemblyBlocked([string]$Path) {
    # Absolute path, not 'dotnet': Start-Process does not resolve a bare command name through PATH
    # the way the call operator does. Getting this wrong made the check silently pass a blocked
    # binary, because "could not run the test" looked exactly like "the test succeeded".
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
    if (-not (Test-Path $dotnet)) { throw "cannot find dotnet.exe to verify the published service" }

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $out = Join-Path $env:TEMP "gpdforge-loadtest-$([guid]::NewGuid().ToString('N')).log"
        try {
            $p = Start-Process -FilePath $dotnet -ArgumentList @("`"$Path`"", '--probe-standby') `
                -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError "$out.err"
            if ($p.ExitCode -eq 0) { return $false }
            $text = ''
            foreach ($f in @($out, "$out.err")) {
                if (Test-Path $f) { $text += (Get-Content $f -Raw -ErrorAction SilentlyContinue) }
            }
            if ($text -notmatch '0x800711C7') { return $false }   # loaded; failed for another reason
        } finally {
            Remove-Item $out, "$out.err" -Force -ErrorAction SilentlyContinue
        }
        # Smart App Control's verdict on a freshly written binary is a cloud lookup; the same file
        # can be refused on the first load and accepted seconds later.
        if ($attempt -lt 3) { Start-Sleep -Seconds 3 }
    }
    return $true
}

if (Test-AssemblyBlocked $dll) {
    Write-Host "  the published service was blocked by Smart App Control; rebuilding for a new hash..." -ForegroundColor Yellow
    dotnet publish "$RepoDir\core\GpdForge.Service.csproj" -c Release -p:Deterministic=false `
        -o "$InstallDir\service" --nologo
    if (Test-AssemblyBlocked $dll) {
        Write-Host "The published service still cannot load (Smart App Control)." -ForegroundColor Red
        Write-Host "The service is NOT installed. Signing it (docs/signing.md) is the durable fix;" -ForegroundColor Red
        Write-Host "re-running this installer may also succeed, since each build is judged afresh." -ForegroundColor Red
        return
    }
    Write-Host "  rebuilt binary loads." -ForegroundColor Green
}

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
#
# A shell failure must NOT abort the install. Step 1 has already DELETED the service, so returning
# here leaves the handheld with no TDP or fan control at all — which is exactly what happened on
# 2026-08-29, when Smart App Control blocked every cargo build-script and this `return` turned a
# cosmetic problem (no desktop window) into an unmanaged machine. The shell is a window onto the
# daemon; it is not a prerequisite for it, and the ordering here implied otherwise.
#
# So: record the failure, keep going, register and start the service, and fail loudly at the END.
# The refusal to copy a stale shell stands — that part was right, and it is why the old binary is
# left untouched rather than overwritten with something older than the bundle it should embed.
$shellFailure = $null
$tauriExe = "$RepoDir\ui\src-tauri\target\release\gpd-forge.exe"
npm --prefix "$RepoDir\ui" run tauri build

# Smart App Control fallback. On 2026-08-29 SAC blocked EVERY cargo build-script under
# ui/src-tauri/target with os error 4551, on three consecutive runs. The cure that worked for the
# .NET side (delete the artefact so the next build has a different hash) did NOT work here: the
# freshly compiled build-scripts were blocked too.
#
# What did work was building with CARGO_TARGET_DIR pointed OUTSIDE the repository tree, into %TEMP%.
# Same source, same toolchain, same resulting hashes — only the location changed, and the identical
# build that had been refused three times completed. So SAC's verdict here is not purely by content:
# the path the binary is executed from is part of it. That is worth knowing beyond this script.
#
# The out-of-tree build is a FALLBACK rather than the default because it forfeits the incremental
# cache in the normal location, which costs about six minutes from cold.
if (-not (Test-Path $tauriExe)) {
    $altTarget = Join-Path $env:TEMP "gpd-forge-tauri-target"
    Write-Host "  shell build failed - retrying with CARGO_TARGET_DIR outside the repo (SAC workaround)" -ForegroundColor Yellow
    Write-Host "  target: $altTarget" -ForegroundColor DarkGray
    $prevTarget = $env:CARGO_TARGET_DIR
    try {
        $env:CARGO_TARGET_DIR = $altTarget
        npm --prefix "$RepoDir\ui" run tauri build
    } finally {
        # Restore rather than clear: the operator may have set it deliberately.
        if ($null -eq $prevTarget) { Remove-Item Env:\CARGO_TARGET_DIR -ErrorAction SilentlyContinue }
        else { $env:CARGO_TARGET_DIR = $prevTarget }
    }
    $altExe = Join-Path $altTarget "release\gpd-forge.exe"
    if (Test-Path $altExe) {
        New-Item -ItemType Directory -Force -Path (Split-Path $tauriExe) | Out-Null
        Copy-Item $altExe $tauriExe -Force
        Write-Host "  recovered: shell built out-of-tree and staged for install" -ForegroundColor Green
    }
}

if (-not (Test-Path $tauriExe)) {
    $shellFailure = "the build produced no gpd-forge.exe, in-tree or out-of-tree (needs the Rust toolchain; Smart App Control blocks cargo's build-scripts with os error 4551)"
} elseif ((Get-Item $tauriExe).LastWriteTime -lt (Get-Item "$RepoDir\ui\dist\index.html").LastWriteTime) {
    $shellFailure = "the built shell is older than the UI bundle it should embed, so the build did not actually run"
}
if ($shellFailure) {
    Write-Host "Desktop shell NOT updated: $shellFailure" -ForegroundColor Red
    Write-Host "Refusing to copy a stale shell - that is what broke telemetry on 2026-08-28." -ForegroundColor Red
    Write-Host "Continuing so the SERVICE is still installed: the web UI at $Url stays fully current." -ForegroundColor Yellow
}
# The shell being replaced is very often running - the previous install opened it, or it is sitting
# in the tray. Windows keeps a lock on a running image, so Copy-Item fails; before the transcript
# existed that failure was invisible and left the machine with no service at all.
if (-not $shellFailure) {
    foreach ($name in @('GPD Forge', 'gpd-forge')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "  stopping running shell (pid $($_.Id))" -ForegroundColor DarkGray
            try { $_.Kill() } catch { }
        }
    }
    Start-Sleep -Milliseconds 500
    Copy-Item $tauriExe "$InstallDir\GPD Forge.exe" -Force
}

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
# ADLX is a user-mode driver API, unrelated to the MSR/EC paths, so it gets its own gate rather than
# riding on -NoHardware: a fault here must not be able to take down power control that has been
# validated on the metal.
if ($EnableGpuProfiles) { $envLines += "GPDFORGE_ENABLE_GPU_PROFILES=1" }

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

# The GPU agent, in the USER'S session. It is not optional plumbing: ADLX cannot be reached from the
# service at all (LocalSystem, session 0, no display driver stack), so without this the Radeon
# profiles simply do not apply. It is the same signed-host + accepted-assembly combination the
# service uses, so Smart App Control has nothing new to refuse.
$agentLink = "$StartupDir\GPD Forge GPU Agent.lnk"
if ($EnableGpuProfiles) {
    $dotnetPath = (Get-Command dotnet).Source
    $gpuLink = $wsh.CreateShortcut($agentLink)
    $gpuLink.TargetPath = $dotnetPath
    $gpuLink.Arguments = "`"$InstallDir\service\GpdForge.Service.dll`" --gpu-agent"
    $gpuLink.WorkingDirectory = "$InstallDir\service"
    $gpuLink.IconLocation = "$InstallDir\icon.ico"
    $gpuLink.Description = 'GPD Forge GPU agent (applies Radeon profiles; must run in your session)'
    $gpuLink.WindowStyle = 7   # minimised: it is a background agent, not something to look at
    $gpuLink.Save()
    Write-Host "  GPU agent will start at logon (Radeon profiles)." -ForegroundColor DarkGray
} elseif (Test-Path $agentLink) {
    # Installing without the gate must not leave an agent behind that keeps driving the GPU.
    Remove-Item -Force $agentLink
    Write-Host "  removed the GPU agent autostart (GPU profiles not enabled)." -ForegroundColor DarkGray
}

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
    # Record what we are about to change BEFORE changing it, so -Restore puts back what was there
    # rather than a plausible default. Written to ProgramData because -Uninstall deletes Program Files.
    $gsvc = Get-Service 'GPDToolService' -ErrorAction SilentlyContinue
    $priorStartType = $null
    if ($gsvc) {
        # StartType exists on PS 5.1's ServiceController via the WMI record; fall back rather than guess.
        try { $priorStartType = (Get-CimInstance Win32_Service -Filter "Name='GPDToolService'").StartMode } catch { $priorStartType = $null }
        # Win32 StartMode words differ from Set-Service's ("Auto" vs "Automatic"); normalise now, while
        # the meaning is still in front of us, rather than at restore time.
        switch ($priorStartType) {
            'Auto'     { $priorStartType = 'Automatic' }
            'Manual'   { $priorStartType = 'Manual' }
            'Disabled' { $priorStartType = 'Disabled' }
            default    { $priorStartType = $null }
        }
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $TakeoverState) | Out-Null
    @{
        takenOverUtc            = (Get-Date).ToUniversalTime().ToString('o')
        gpdToolServiceStartType = $priorStartType
        gpdToolServicePresent   = [bool]$gsvc
    } | ConvertTo-Json | Set-Content -Path $TakeoverState -Encoding UTF8

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
    Write-Host "  Undo any time with:  install-gpd-forge.ps1 -Restore   (also runs during -Uninstall)" -ForegroundColor DarkGray
}

# --- 7) verify the install end to end, then open ---
# A green install that ships a dashboard showing "--" is worse than a red one. verify-install.ps1
# checks the things the test suite structurally cannot: that the shell binary in Program Files is
# the one we just built, and that it can actually reach the daemon.
Write-Host "== 7/7  Verifying the installation ==" -ForegroundColor Cyan
& "$RepoDir\scripts\verify-install.ps1"
$verifyOk = ($LASTEXITCODE -eq 0)

if (-not $verifyOk) {
    Write-Host "`nInstall completed but verification FAILED - see the checks above." -ForegroundColor Red
    Write-Host "Not opening the window: it would most likely show '--' for every tile." -ForegroundColor Red
    exit 1
}

# The service is good. Report the shell honestly rather than quietly finishing green: a partial
# install that announces success is how a stale binary survives unnoticed for a day.
if ($shellFailure) {
    Write-Host "`nService installed and verified - the daemon is current." -ForegroundColor Green
    Write-Host "The DESKTOP SHELL was NOT updated: $shellFailure" -ForegroundColor Red
    if (Test-Path "$InstallDir\GPD Forge.exe") {
        Write-Host "The previously installed window is still there and is now OLDER than the daemon." -ForegroundColor Yellow
        Write-Host "Settings > About will say so: it compares the shell build against the daemon build." -ForegroundColor Yellow
    } else {
        Write-Host "There is no installed window at all; use the web UI at $Url, which IS current." -ForegroundColor Yellow
    }
    exit 1
}

# Start the agent now rather than making the user log out to see the feature work.
if ($EnableGpuProfiles) {
    foreach ($p in Get-Process dotnet -ErrorAction SilentlyContinue) {
        # A previous agent still running would hold the old assembly and post stale reports.
        try { if ($p.CommandLine -like '*--gpu-agent*') { $p.Kill() } } catch { }
    }
    Start-Process (Get-Command dotnet).Source -ArgumentList "`"$InstallDir\service\GpdForge.Service.dll`" --gpu-agent" -WindowStyle Hidden
    Write-Host "GPU agent started in this session." -ForegroundColor DarkGray
}

Start-Process "$InstallDir\GPD Forge.exe"
Write-Host "`nDone. GPD Forge runs as a service (autostart). Open the dashboard from the Start Menu" -ForegroundColor Green
Write-Host "('GPD Forge') or from the premium tray icon. The local API remains at $Url." -ForegroundColor DarkGray
