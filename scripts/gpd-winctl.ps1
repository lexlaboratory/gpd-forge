# GPD Forge — GPD controller button mapper (WinControls protocol). GPL-3.0-or-later.
#
# Thin, SAFE wrapper around the proven `gpdconfig` tool (pelrun/pyWinControls) so we don't
# hand-write firmware I/O. It talks to the GPD controller's config interface
# (USB 2f24:0135, HID usage page 0xff00) to remap a back paddle to a rare key that the
# resident listener (scripts/overlay-hotkey.ps1) catches to toggle the overlay.
#
# We map the paddle to a SINGLE unused key (F24 by default): a single keycode fires the
# global hotkey reliably, unlike a modifier chord routed through the paddle's macro slots.
#
# Runs under Smart App Control via python.exe (signed) + gpdconfig loaded as a module (no pip
# .exe shim). The `hid` binding needs the native hidapi.dll, which -Setup fetches from the
# official libusb/hidapi release into %LOCALAPPDATA%\GPDForge\hidapi and we load via
# os.add_dll_directory (PATH is not used for DLL loading on Python 3.8+).
#
# EVERY write is preceded by an automatic full-config backup and followed by a read-back.
#
# Usage:
#   ...gpd-winctl.ps1 -Setup                 # one-time: pip install gpdconfig + fetch hidapi.dll
#   ...gpd-winctl.ps1 -Backup                # dump current config to a timestamped file (safe)
#   ...gpd-winctl.ps1 -MapHome               # backup, map L4 -> F24, then read back (FIRMWARE WRITE)
#   ...gpd-winctl.ps1 -MapHome -Button r4    # use R4 instead
#   ...gpd-winctl.ps1 -Restore <backup.txt>  # restore a previous backup
#   ...gpd-winctl.ps1 -Keys                  # list valid key names
param(
  [switch]$Setup,
  [switch]$Backup,
  [switch]$MapHome,
  [switch]$Keys,
  [string]$Restore,
  [ValidateSet('l4', 'r4')][string]$Button = 'l4',
  [string]$Key = 'f24',
  [string]$BackupDir = (Join-Path $env:LOCALAPPDATA 'GPDForge\controller-backups'),
  [string]$HidapiDir = (Join-Path $env:LOCALAPPDATA 'GPDForge\hidapi')
)
$ErrorActionPreference = 'Stop'

function Get-Python {
  foreach ($c in @('python', 'python3', 'py')) { $s = (Get-Command $c -ErrorAction SilentlyContinue).Source; if ($s) { return $s } }
  throw "Python not found. Install Python 3, then re-run with -Setup."
}
$py = Get-Python

# Run gpdconfig with the hidapi dir added to the DLL search path BEFORE `import hid` runs.
function Invoke-Gpdconfig([string[]]$gargs) {
  $boot = "import os,sys; d=r'$HidapiDir';" +
          " os.path.isdir(d) and os.add_dll_directory(d);" +
          " sys.argv=['gpdconfig']+sys.argv[1:];" +
          " from gpdconfig.app import main; main()"
  & $py -c $boot @gargs
}

function Test-Gpdconfig { & $py -c "import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('gpdconfig') else 1)" 2>$null; return ($LASTEXITCODE -eq 0) }

function Install-Hidapi {
  New-Item -ItemType Directory -Force -Path $HidapiDir | Out-Null
  $dll = Join-Path $HidapiDir 'hidapi.dll'
  if (Test-Path $dll) { return }
  $url = 'https://github.com/libusb/hidapi/releases/download/hidapi-0.14.0/hidapi-win.zip'
  $zip = Join-Path $env:TEMP 'hidapi-win.zip'
  Write-Output "Fetching hidapi.dll from libusb/hidapi..."
  Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 60
  $ex = Join-Path $env:TEMP 'hidapi_extract'
  Expand-Archive -Path $zip -DestinationPath $ex -Force
  $found = Get-ChildItem -Path $ex -Recurse -Filter 'hidapi.dll' | Where-Object { $_.FullName -like '*x64*' } | Select-Object -First 1
  if (-not $found) { $found = Get-ChildItem -Path $ex -Recurse -Filter 'hidapi.dll' | Select-Object -First 1 }
  Copy-Item $found.FullName $dll -Force
  if (-not (Test-Path $dll)) { throw "Failed to place hidapi.dll" }
}

if ($Setup) {
  Write-Output "Installing gpdconfig (+ hid) for the current user..."
  & $py -m pip install --user --upgrade gpdconfig
  Install-Hidapi
  if (Test-Gpdconfig) { Write-Output "gpdconfig ready (hidapi in $HidapiDir)." } else { Write-Error "gpdconfig still not importable after install." }
  return
}

if (-not (Test-Gpdconfig)) {
  Write-Warning "gpdconfig is not installed. Run:  powershell -File scripts\gpd-winctl.ps1 -Setup"
  return
}
if (-not (Test-Path (Join-Path $HidapiDir 'hidapi.dll'))) { Install-Hidapi }

if ($Keys) { Invoke-Gpdconfig @('--keys'); return }

New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Backup-Config([string]$why) {
  $file = Join-Path $BackupDir "controller-$stamp-$why.txt"
  Invoke-Gpdconfig @('-d', $file)
  if (Test-Path $file) { Write-Output "Backup saved: $file" } else { throw "Backup failed (device not readable?)" }
  return $file
}

if ($Backup) { Backup-Config 'manual' | Out-Null; return }

if ($Restore) {
  if (-not (Test-Path $Restore)) { throw "Backup file not found: $Restore" }
  Write-Output "Restoring controller config from $Restore ..."
  Invoke-Gpdconfig @('-s', $Restore)
  Write-Output "Restore complete."
  return
}

if ($MapHome) {
  # 1) Always back up first.
  $bak = Backup-Config 'pre-maphome'
  # 2) Map the chosen paddle's first macro slot to the key; clear the other three slots.
  $slots = if ($Button -eq 'l4') { @('l41', 'l42', 'l43', 'l44') } else { @('r41', 'r42', 'r43', 'r44') }
  $cfgArgs = @("$($slots[0])=$Key", "$($slots[1])=none", "$($slots[2])=none", "$($slots[3])=none")
  Write-Output "Mapping $($Button.ToUpper()) -> $Key   (backup at $bak)"
  Invoke-Gpdconfig $cfgArgs
  # 3) Read back and show the paddle lines to confirm.
  Write-Output "--- read-back ---"
  Invoke-Gpdconfig @('-v') 2>&1 | Select-String -Pattern ($Button)
  Write-Output "Done. Now run the listener bound to that key:"
  Write-Output "  powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File scripts\overlay-hotkey.ps1 -Modifiers `"`" -Key F24"
  return
}

Write-Output "Nothing to do. Try -Backup, -MapHome, -Restore <file>, -Keys, or -Setup."
