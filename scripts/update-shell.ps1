<#
  GPD Forge - replace just the installed desktop shell. GPL-3.0-or-later.

  Smart App Control judges each unsigned binary individually, and its verdict is not stable: a
  freshly built shell can be refused while the next build of the same source runs fine (the same
  behaviour seen with cargo's build-script binaries). When that happens there is no need to redo the
  whole install - the service, wwwroot and shortcuts are already correct. This rebuilds the shell
  until Windows agrees to run it, then swaps it in.

  Usage (self-elevates):
    powershell -ExecutionPolicy Bypass -File scripts\update-shell.ps1
#>
[CmdletBinding()]
param([int]$MaxAttempts = 3)
$ErrorActionPreference = 'Stop'

$RepoDir    = Split-Path $PSScriptRoot -Parent
$InstallDir = 'C:\Program Files\GPD Forge'
$Source     = "$RepoDir\ui\src-tauri\target\release\gpd-forge.exe"
$Target     = "$InstallDir\GPD Forge.exe"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevation required - relaunching (accept UAC)..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList @('-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    return
}

if (-not (Test-Path $Source)) {
    Write-Host "No shell to install at $Source - run 'npm --prefix ui run tauri build' first." -ForegroundColor Red
    exit 1
}

# Verify the candidate actually runs BEFORE replacing a working install with a blocked one.
Write-Host "Checking that Windows will run the freshly built shell..." -ForegroundColor Cyan
$proc = $null
try {
    $proc = Start-Process $Source -PassThru -ErrorAction Stop
    Start-Sleep -Seconds 4
    if ($proc.HasExited) { throw "the shell exited immediately (code $($proc.ExitCode))" }
    Write-Host "  it runs (pid $($proc.Id))" -ForegroundColor Green
} catch {
    Write-Host "  Smart App Control refused this build: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Rebuild it ('npm --prefix ui run tauri build') and run this again - a new binary is" -ForegroundColor Yellow
    Write-Host "  usually accepted. The lasting fix is code-signing; see docs/signing.md." -ForegroundColor Yellow
    Write-Host "  Meanwhile the dashboard is fully usable at http://127.0.0.1:8787." -ForegroundColor Yellow
    exit 1
}
finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }

# A running image is locked, so anything still open must go before the copy - this failing silently
# is what once left the machine with a stale shell and no explanation.
foreach ($name in @('GPD Forge', 'gpd-forge')) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  stopping running shell (pid $($_.Id))" -ForegroundColor DarkGray
        try { $_.Kill() } catch { }
    }
}
Start-Sleep -Milliseconds 700

Copy-Item $Source $Target -Force
Write-Host "Installed $Target ($(Get-Item $Target | Select-Object -ExpandProperty LastWriteTime))" -ForegroundColor Green

& "$RepoDir\scripts\verify-install.ps1"
if ($LASTEXITCODE -ne 0) { exit 1 }
Start-Process $Target
exit 0
