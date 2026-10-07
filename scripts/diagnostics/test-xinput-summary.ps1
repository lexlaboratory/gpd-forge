$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\xinput-summary.ps1"

$low = Get-XInputTriggerAssessment -Samples @(0, 80, 180, 230, 100, 0)
if ($low.Max -ne 230 -or $low.CriteriaMet) { throw 'max 230 fixture must not pass saturation criteria' }

$good = Get-XInputTriggerAssessment -Samples @(0, 252, 255, 253, 251, 4, 0)
if (-not $good.CriteriaMet) { throw 'full travel, held >=250, then release <=5 fixture must pass' }

$disconnected = Get-XInputTriggerAssessment -Samples @()
if ($null -ne $disconnected.Min -or $null -ne $disconnected.Max -or $disconnected.CriteriaMet) {
  throw 'disconnected fixture must retain unavailable range, never report zero or pass'
}

'PASS: max 230 does not pass saturation; verified full travel/hold/release passes; disconnected range remains unavailable.'
