# GPD Forge — overlay launcher. GPL-3.0-or-later.
#
# Opens the Quick Access Menu (/overlay.html served by the daemon) as a borderless,
# right-docked browser app-window. Uses msedge/chrome, which are Microsoft/Google-signed,
# so it runs under Smart App Control (our own unsigned Tauri exe does not — see ROADMAP).
#
# Bind this to your chosen "Home" button: map a back paddle (L4/R4) or Menu to a rare
# hotkey with GPD WinControls, then trigger this script from that hotkey (a resident
# listener is the next step; see docs). Run standalone to test:
#   powershell -ExecutionPolicy Bypass -File scripts\overlay-launch.ps1
param(
  [int]$Width = 380,
  [string]$Url = "http://127.0.0.1:8787/overlay.html"
)

$ErrorActionPreference = "Stop"

# Prefer Edge, fall back to Chrome (both are code-signed → SAC-allowed).
$candidates = @(
  (Get-Command msedge.exe -ErrorAction SilentlyContinue).Source,
  "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
  "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
  (Get-Command chrome.exe -ErrorAction SilentlyContinue).Source,
  "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
) | Where-Object { $_ -and (Test-Path $_) }

if (-not $candidates) { Write-Error "No signed browser (Edge/Chrome) found to host the overlay."; exit 1 }
$browser = $candidates[0]

# Dock to the right edge of the primary screen.
Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$x = $screen.Width - $Width
$h = $screen.Height

# A dedicated profile dir keeps the app-window free of tabs/bookmarks UI.
$profile = Join-Path $env:LOCALAPPDATA "GPDForge\overlay-profile"
New-Item -ItemType Directory -Force -Path $profile | Out-Null

& $browser `
  "--app=$Url" `
  "--user-data-dir=$profile" `
  "--window-size=$Width,$h" `
  "--window-position=$x,0" `
  "--new-window" `
  "--no-first-run" `
  "--disable-features=Translate" | Out-Null

Write-Output "GPD Forge overlay launched ($Width px, right-docked) -> $Url"
