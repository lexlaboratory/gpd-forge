[CmdletBinding()]
param(
  [ValidateRange(1,1440)][int]$DurationMinutes = 10,
  [ValidateRange(5,3600)][int]$IntervalSeconds = 5,
  [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18') { throw 'This user-run watcher refuses to run as LocalSystem.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$privateRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'out\repair-validation\power-events'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = $privateRoot }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (-not $outputRoot.StartsWith($privateRoot + '\',[StringComparison]::OrdinalIgnoreCase) -and -not [string]::Equals($outputRoot,$privateRoot,[StringComparison]::OrdinalIgnoreCase)) { throw "Output directory must stay under $privateRoot" }
if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) { New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null }
$maxFolderBytes = 128MB
$existingBytes = [long](Get-ChildItem -LiteralPath $outputRoot -File -Recurse -Force -ErrorAction Stop | Measure-Object -Property Length -Sum).Sum
if ($existingBytes -ge $maxFolderBytes) { throw "Output directory already contains $existingBytes bytes (limit $maxFolderBytes); preserving existing evidence and refusing to start." }
$mutex = [Threading.Mutex]::new($false,'Local\GpdForgePowerEventsWatcher')
$hasMutex = $false
try { $hasMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $hasMutex = $true }
if (-not $hasMutex) { $mutex.Dispose(); throw 'A power-events watcher is already running in this user session.' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logPath = Join-Path $outputRoot "power-events-$stamp-$([guid]::NewGuid().ToString('N').Substring(0,8)).jsonl"
$maxBytes = 32MB
$apiBase = 'http://127.0.0.1:8787'
$watchStarted = [DateTimeOffset]::Now
$watchClock = [Diagnostics.Stopwatch]::StartNew()
$stream = $null
$bytesWritten = 0L
$script:outputRoot = $outputRoot
$sampleCount = 0
$eventSeen = @{}
$stopReason = 'duration'
function Add-DurableJsonLine($Object) {
  $line = ($Object | ConvertTo-Json -Depth 8 -Compress) + "`n"
  $encoded = [Text.UTF8Encoding]::new($false).GetBytes($line)
  if ($script:bytesWritten + $encoded.Length -gt $script:maxBytes) { return $false }
  $folderBytes = [long](Get-ChildItem -LiteralPath $script:outputRoot -File -Recurse -Force -ErrorAction Stop | Measure-Object -Property Length -Sum).Sum
  if ($folderBytes + $encoded.Length -gt $script:maxFolderBytes) { return $false }
  $script:stream.Write($encoded,0,$encoded.Length)
  $script:stream.Flush($true)
  $script:bytesWritten += $encoded.Length
  return $true
}
function Get-ApiSnapshot([string]$Path) {
  try {
    $response = Invoke-WebRequest -Uri ($script:apiBase + $Path) -Method Get -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop
    if ($response.Headers['Content-Type'] -notmatch '^application/(.+\+)?json(?:\s*;|$)') { throw "Unexpected content type '$($response.Headers['Content-Type'])' (not JSON)." }
    $value = $response.Content | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $value -or $value -is [string] -or $value -is [ValueType] -or $value -is [array]) { throw 'Endpoint returned non-object JSON.' }
    return [pscustomobject]@{ status='ok'; data=$value; error=$null }
  } catch {
    return [pscustomobject]@{ status='unknown'; data=$null; error=$_.Exception.Message }
  }
}
function Get-BatterySnapshot {
  try {
    $rows = @(Get-CimInstance -Namespace 'root/WMI' -ClassName BatteryStatus -ErrorAction Stop | ForEach-Object {
      [pscustomobject]@{ instanceName=$_.InstanceName; active=$_.Active; powerOnline=$_.PowerOnline; charging=$_.Charging; discharging=$_.Discharging; voltageMv=$_.Voltage; remainingCapacityMWh=$_.RemainingCapacity; chargeRateMW=$_.ChargeRate; dischargeRateMW=$_.DischargeRate; critical=$_.Critical }
    })
    if ($rows.Count -eq 0) { return [pscustomobject]@{ status='unknown'; readings=@(); error='BatteryStatus returned no instances.' } }
    return [pscustomobject]@{ status='ok'; readings=$rows; error=$null; rateNote='Raw WMI rate values preserved; zero/null may be device-specific and is not interpreted as power flow.' }
  } catch { return [pscustomobject]@{ status='unknown'; readings=@(); error=$_.Exception.Message } }
}
function Convert-Event($Event, [string]$Phase) {
  $data = @($Event.Properties | ForEach-Object { if ($null -eq $_.Value) { $null } else { [string]$_.Value } })
  [pscustomobject]@{ phase=$Phase; id=$Event.Id; provider=$Event.ProviderName; recordId=$Event.RecordId; timeUtc=$Event.TimeCreated.ToUniversalTime().ToString('o'); timeLocal=$Event.TimeCreated.ToString('o'); eventData=$data }
}
function Read-SystemEvents([DateTime]$Since, [int]$MaxEvents) {
  $events = @()
  $errors = @()
  $queryErrors = @()
  $ordinary = @(Get-WinEvent -FilterHashtable @{LogName='System';StartTime=$Since;Id=@(41,6008,1001)} -MaxEvents $MaxEvents -ErrorAction SilentlyContinue -ErrorVariable +queryErrors)
  $errors += @($queryErrors); $queryErrors = @()
  $whea = @(Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-WHEA-Logger';StartTime=$Since} -MaxEvents $MaxEvents -ErrorAction SilentlyContinue -ErrorVariable +queryErrors)
  $errors += @($queryErrors)
  $events = @($ordinary + $whea | Sort-Object RecordId -Unique | Select-Object -First $MaxEvents)
  $realErrors = @($errors | Where-Object { $_.FullyQualifiedErrorId -notmatch '^NoMatchingEventsFound(?:,|$)' })
  if ($realErrors.Count) { return [pscustomobject]@{ status='unknown'; events=$events; error=(@($realErrors | ForEach-Object { $_.Exception.Message }) -join ' | ') } }
  if ($events.Count -eq 0) { return [pscustomobject]@{ status='noEvents'; events=@(); error=$null } }
  [pscustomobject]@{ status='ok'; events=$events; error=$null }
}
$boot = try {
  $lastBoot = (Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime
  if ($null -eq $lastBoot) { throw 'Win32_OperatingSystem.LastBootUpTime is null.' }
  [pscustomobject]@{ status='ok'; lastBootUtc=([DateTime]$lastBoot).ToUniversalTime().ToString('o'); lastBootLocal=([DateTime]$lastBoot).ToString('o'); error=$null }
} catch { [pscustomobject]@{ status='unknown'; lastBootUtc=$null; lastBootLocal=$null; error=$_.Exception.Message } }
try {
  $stream = [IO.FileStream]::new($logPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
  $header = [pscustomobject]@{ kind='header'; schemaVersion=1; startedUtc=$watchStarted.UtcDateTime.ToString('o'); startedLocal=$watchStarted.ToString('o'); lastBoot=$boot; durationMinutes=$DurationMinutes; intervalSeconds=$IntervalSeconds; source='read-only GPD Forge local APIs + root/WMI BatteryStatus + System event log'; eventsLookbackHours=24; maxFileBytes=$maxBytes; maxFolderBytes=$maxFolderBytes }
  if (-not (Add-DurableJsonLine $header)) { throw 'Output directory or file size limit reached before header; no existing logs were removed.' }
  $baseline = Read-SystemEvents (Get-Date).AddHours(-24) 200
  foreach ($event in $baseline.events) {
    $eventSeen[[string]$event.RecordId] = $true
    if (-not (Add-DurableJsonLine ([pscustomobject]@{kind='event';capturedUtc=[DateTimeOffset]::UtcNow.ToString('o');event=(Convert-Event $event 'baseline')}))) { $stopReason='sizeLimit'; break }
  }
  if ($baseline.status -ne 'ok') {
    if (-not (Add-DurableJsonLine ([pscustomobject]@{kind='eventQuery';phase='baseline';capturedUtc=[DateTimeOffset]::UtcNow.ToString('o');status=$baseline.status;error=$baseline.error}))) { $stopReason='sizeLimit' }
  }
  while ($watchClock.Elapsed.TotalMinutes -lt $DurationMinutes -and $stopReason -ne 'sizeLimit') {
    $captured = [DateTimeOffset]::Now
    $eventResult = Read-SystemEvents $watchStarted.LocalDateTime 200
    $newEvents = @()
    foreach ($event in $eventResult.events) {
      $key = [string]$event.RecordId
      if (-not $eventSeen.ContainsKey($key)) { $eventSeen[$key]=$true; $newEvents += Convert-Event $event 'duringWatch' }
    }
    $record = [pscustomobject]@{
      kind='sample'; sequence=($sampleCount + 1); capturedUtc=$captured.UtcDateTime.ToString('o'); capturedLocal=$captured.ToString('o'); elapsedSeconds=[Math]::Round($watchClock.Elapsed.TotalSeconds,2)
      api=[ordered]@{ telemetry=(Get-ApiSnapshot '/telemetry'); tdp=(Get-ApiSnapshot '/tdp'); fan=(Get-ApiSnapshot '/fan'); version=(Get-ApiSnapshot '/version') }
      battery=(Get-BatterySnapshot); eventQuery=[pscustomobject]@{status=$eventResult.status;error=$eventResult.error}; events=$newEvents
    }
    if (-not (Add-DurableJsonLine $record)) { $stopReason='sizeLimit'; break }
    $sampleCount++
    $remaining = [Math]::Min($IntervalSeconds,[Math]::Max(0.1,($DurationMinutes * 60) - $watchClock.Elapsed.TotalSeconds))
    if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Max(100,$remaining * 1000)) }
  }
} finally {
  if ($stream) {
    try { [void](Add-DurableJsonLine ([pscustomobject]@{kind='footer';endedUtc=[DateTimeOffset]::UtcNow.ToString('o');elapsedSeconds=[Math]::Round($watchClock.Elapsed.TotalSeconds,2);sampleCount=$sampleCount;stopReason=$stopReason;bytesWritten=$bytesWritten})) } catch {}
    $stream.Dispose()
  }
  if ($hasMutex) { $mutex.ReleaseMutex() }
  $mutex.Dispose()
}
[pscustomobject]@{ path=$logPath; samples=$sampleCount; stopReason=$stopReason; bytes=$bytesWritten; durationSeconds=[Math]::Round($watchClock.Elapsed.TotalSeconds,2) }
