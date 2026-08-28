<#
  GPD Forge - fetch Intel PresentMon for FPS telemetry. GPL-3.0-or-later.

  Downloads the official signed PresentMon console binary and refuses to keep it unless Windows
  reports a valid Authenticode signature from Intel Corporation. That check is not ceremony: this
  machine runs Smart App Control in Enforcement, which will not execute an unsigned binary, and a
  probe that silently ships something unrunnable is worse than no probe at all.

  PresentMon is MIT-licensed (compatible with this project's GPL-3.0) and already attributed in
  NOTICE and docs/CREDITS.md. It is downloaded rather than committed - see .gitignore.

  Usage:
    powershell -ExecutionPolicy Bypass -File scripts\fetch-presentmon.ps1
    ...\fetch-presentmon.ps1 -Force     # re-download even if it is already present
#>
[CmdletBinding()]
param(
    [string]$Version = '2.5.1',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

$RepoDir   = Split-Path $PSScriptRoot -Parent
$TargetDir = Join-Path $RepoDir 'vendor\presentmon'
$Target    = Join-Path $TargetDir 'PresentMon.exe'
$Url       = "https://github.com/GameTechDev/PresentMon/releases/download/v$Version/PresentMon-$Version-x64.exe"

if ((Test-Path $Target) -and -not $Force) {
    Write-Host "PresentMon already present: $Target" -ForegroundColor Green
    exit 0
}

New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
$staging = Join-Path $TargetDir 'PresentMon.exe.download'

Write-Host "Downloading PresentMon $Version ..." -ForegroundColor Cyan
$prevProgress = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'
try {
    Invoke-WebRequest -UseBasicParsing $Url -OutFile $staging
} finally {
    $ProgressPreference = $prevProgress
}

$sig = Get-AuthenticodeSignature $staging
if ($sig.Status -ne 'Valid') {
    Remove-Item -Force $staging
    Write-Host "REJECTED: Authenticode status is '$($sig.Status)', expected 'Valid'." -ForegroundColor Red
    exit 1
}
$subject = $sig.SignerCertificate.Subject
if ($subject -notmatch 'O=Intel Corporation') {
    Remove-Item -Force $staging
    Write-Host "REJECTED: signed by '$subject', expected Intel Corporation." -ForegroundColor Red
    exit 1
}

Move-Item -Force $staging $Target
Write-Host "  signer : $subject" -ForegroundColor DarkGray
Write-Host "  sha256 : $((Get-FileHash $Target -Algorithm SHA256).Hash)" -ForegroundColor DarkGray
Write-Host "PresentMon verified and installed at $Target" -ForegroundColor Green
exit 0
