param(
  [ValidateRange(1,30)][int]$DurationSeconds = 30,
  [ValidateRange(20,1000)][int]$IntervalMilliseconds = 100,
  [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\xinput-summary.ps1"

if (-not ('GpdXInputNative' -as [type])) {
  Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)] public struct GpdXInputGamepad {
  public ushort wButtons;
  public byte bLeftTrigger;
  public byte bRightTrigger;
  public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
}
[StructLayout(LayoutKind.Sequential)] public struct GpdXInputState {
  public uint dwPacketNumber;
  public GpdXInputGamepad Gamepad;
}
public static class GpdXInputNative {
  [DllImport("xinput1_4.dll", EntryPoint="XInputGetState")]
  public static extern uint GetState(uint dwUserIndex, out GpdXInputState pState);
}
"@
}

$diagnosticsRoot = [IO.Path]::GetFullPath($PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
  $OutputPath = Join-Path $diagnosticsRoot "xinput-triggers-$(Get-Date -Format 'yyyyMMdd-HHmmss').csv"
} else {
  $OutputPath = [IO.Path]::GetFullPath($OutputPath)
}
$rootPrefix = $diagnosticsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $OutputPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
  throw "OutputPath must stay inside $diagnosticsRoot"
}

Write-Host "XInput trigger capture: $DurationSeconds seconds, slots 0-3, interval $IntervalMilliseconds ms."
Write-Host 'Keep L2/R2 released for the 1-second idle enumeration. When capture starts, press each trigger fully, hold briefly, then release it.'
Write-Host 'Only XInput trigger bytes are captured; no keyboard, other buttons, configuration writes, or calibration.'
$connectedDuringIdle = [System.Collections.Generic.HashSet[int]]::new()
$idleWatch = [Diagnostics.Stopwatch]::StartNew()
while ($idleWatch.Elapsed.TotalSeconds -lt 1) {
  foreach ($slot in 0..3) {
    $state = [GpdXInputState]::new()
    if ([GpdXInputNative]::GetState([uint32]$slot, [ref]$state) -eq 0) { [void]$connectedDuringIdle.Add($slot) }
  }
  Start-Sleep -Milliseconds 100
}
$idleWatch.Stop()
Write-Host ('Idle XInput slots connected: ' + (@($connectedDuringIdle | Sort-Object) -join ', '))
Write-Host 'Capture is starting now.'

$rows = [System.Collections.Generic.List[object]]::new()
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $DurationSeconds) {
  $utc = [DateTime]::UtcNow.ToString('o')
  $local = [DateTimeOffset]::Now.ToString('o')
  foreach ($slot in 0..3) {
    $state = [GpdXInputState]::new()
    $connected = ([GpdXInputNative]::GetState([uint32]$slot, [ref]$state) -eq 0)
    $lt = $null; $rt = $null
    if ($connected) {
      $lt = [int]$state.Gamepad.bLeftTrigger
      $rt = [int]$state.Gamepad.bRightTrigger
    }
    $rows.Add([pscustomobject]@{ TimestampUtc=$utc; TimestampLocal=$local; Slot=$slot; Connected=$connected; LeftTrigger=$lt; RightTrigger=$rt })
  }
  $remainingMs = [int][Math]::Max(0, ($DurationSeconds - $sw.Elapsed.TotalSeconds) * 1000)
  if ($remainingMs -gt 0) { Start-Sleep -Milliseconds ([Math]::Min($IntervalMilliseconds, $remainingMs)) }
}
$sw.Stop()
$rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding utf8
Write-Host "ElapsedSeconds=$([Math]::Round($sw.Elapsed.TotalSeconds,2)) Csv=$OutputPath"
foreach ($slot in 0..3) {
  $slotRows = @($rows | Where-Object { $_.Slot -eq $slot })
  $connectedRows = @($slotRows | Where-Object Connected)
  if ($connectedRows.Count -eq 0) {
    Write-Host "Slot=$slot ConnectedSamples=0; LT/RT min/max unavailable, not treated as zero."
    continue
  }
  foreach ($trigger in @(@('L2','LeftTrigger'),@('R2','RightTrigger'))) {
    $assessment = Get-XInputTriggerAssessment -Samples @($connectedRows | ForEach-Object { [int]$_.$($trigger[1]) })
    Write-Host "$($trigger[0]) Slot=$slot Samples=$($connectedRows.Count) Min=$($assessment.Min) Max=$($assessment.Max) Reached255=$($assessment.Reached255) HeldAtLeast250=$($assessment.HeldAtLeast250) ReleasedTo5AfterPeak=$($assessment.ReleasedTo5AfterPeak) CriteriaMet=$($assessment.CriteriaMet)"
  }
}
