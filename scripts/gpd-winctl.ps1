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
# Runs under Smart App Control via python.exe (signed) + `python -m gpdconfig` (no pip .exe shim).
# EVERY write is preceded by an automatic full-config backup and followed by a read-back.
#
# Usage:
#   ...gpd-winctl.ps1 -Setup                 # one-time: pip install --user gpdconfig
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
  [string]$BackupDir = (Join-Path $env:LOCALAPPDATA 'GPDForge\controller-backups')
)
$ErrorActionPreference = 'Stop'

function Get-Python {
  foreach ($c in @('python', 'python3', 'py')) { $s = (Get-Command $c -ErrorAction SilentlyContinue).Source; if ($s) { return $s } }
  throw "Python not found. Install Python 3, then re-run with -Setup."
}
$py = Get-Python

function Test-Gpdconfig { & $py -c "import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('gpdconfig') else 1)" 2>$null; return ($LASTEXITCODE -eq 0) }

if ($Setup) {
  Write-Output "Installing gpdconfig (+ hidapi) for the current user..."
  & $py -m pip install --user --upgrade gpdconfig
  if (Test-Gpdconfig) { Write-Output "gpdconfig ready." } else { Write-Error "gpdconfig still not importable after install." }
  return
}

if (-not (Test-Gpdconfig)) {
  Write-Warning "gpdconfig is not installed. Run:  powershell -File scripts\gpd-winctl.ps1 -Setup"
  return
}

if ($Keys) { & $py -m gpdconfig --keys; return }

New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Backup-Config([string]$why) {
  $file = Join-Path $BackupDir "controller-$stamp-$why.txt"
  & $py -m gpdconfig -d $file
  if (Test-Path $file) { Write-Output "Backup saved: $file" } else { throw "Backup failed (device not readable?)" }
  return $file
}

if ($Backup) { Backup-Config 'manual' | Out-Null; return }

if ($Restore) {
  if (-not (Test-Path $Restore)) { throw "Backup file not found: $Restore" }
  Write-Output "Restoring controller config from $Restore ..."
  & $py -m gpdconfig -s $Restore
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
  & $py -m gpdconfig @cfgArgs
  # 3) Read back and show the paddle lines to confirm.
  Write-Output "--- read-back ---"
  & $py -m gpdconfig -v 2>&1 | Select-String -Pattern ($Button)
  Write-Output "Done. Now run the listener bound to that key:"
  Write-Output "  powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File scripts\overlay-hotkey.ps1 -Modifiers `"`" -Key F24"
  return
}

Write-Output "Nothing to do. Try -Backup, -MapHome, -Restore <file>, -Keys, or -Setup."
