[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidateSet('Status','BackupStatus','StopForge','StartForge','LaunchMA','StopMA','RestoreService')]
  [string]$Action,
  [ValidateRange(1024,65535)][int]$Port = 8787,
  [string]$SnapshotPath
)
$ErrorActionPreference = 'Stop'

$script:ForgeServiceName = 'GPDForge'
$script:MotionAssistantPath = 'C:\Program Files\Motion Assistant\MotionAssistant.exe'
$script:DiagnosticsRoot = [IO.Path]::GetFullPath($PSScriptRoot)

function Assert-Administrator {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = [Security.Principal.WindowsPrincipal]::new($identity)
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This action requires an elevated PowerShell session.'
  }
}

function Get-ForgeServiceInfo {
  $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$script:ForgeServiceName'"
  if (-not $svc) { throw "Service '$script:ForgeServiceName' not found." }
  $key = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$script:ForgeServiceName"
  [pscustomobject]@{
    Name = $svc.Name
    DisplayName = $svc.DisplayName
    State = $svc.State
    ProcessId = [int]$svc.ProcessId
    StartMode = $svc.StartMode
    StartName = $svc.StartName
    PathName = $svc.PathName
    Registry = [pscustomobject]@{
      Start = $key.Start
      Type = $key.Type
      ErrorControl = $key.ErrorControl
      ImagePath = $key.ImagePath
      ObjectName = $key.ObjectName
      DisplayName = $key.DisplayName
      DelayedAutoStart = $key.DelayedAutoStart
      DependOnService = @($key.DependOnService)
    }
  }
}

function Get-ReadOnlyApiState {
  param([bool]$ForgeRunning)
  $result = [ordered]@{}
  foreach ($route in @('health','version','mode','fan')) {
    if (-not $ForgeRunning) {
      $result[$route] = [pscustomobject]@{ Available=$false; Detail='GPD Forge service stopped' }
      continue
    }
    try {
      $value = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:$Port/$route" -TimeoutSec 2
      $result[$route] = [pscustomobject]@{ Available=$true; Value=$value }
    } catch {
      $result[$route] = [pscustomobject]@{ Available=$false; Detail=$_.Exception.Message }
    }
  }
  [pscustomobject]$result
}

function Get-Inventory {
  $svc = Get-ForgeServiceInfo
  $wmiProcesses = @(Get-CimInstance -ClassName Win32_Process | Where-Object {
    $_.Name -match '^(MotionAssistant|GPDTool|GPDToolService|GPDKeyboard|GPD Forge)\.exe$' -or
    ($svc.ProcessId -gt 0 -and $_.ProcessId -eq $svc.ProcessId)
  } | ForEach-Object {
    [pscustomobject]@{ Name=$_.Name; ProcessId=[int]$_.ProcessId; ExecutablePath=$_.ExecutablePath }
  })
  $motion = @($wmiProcesses | Where-Object { $_.Name -ieq 'MotionAssistant.exe' })
  $drivers = @(Get-CimInstance -ClassName Win32_SystemDriver | Where-Object {
    $_.Name -in @('PawnIO','inpoutx64','R0MotionAssistant','ViGEmBus')
  } | ForEach-Object {
    [pscustomobject]@{ Name=$_.Name; State=$_.State; StartMode=$_.StartMode; PathName=$_.PathName }
  } | Sort-Object Name)
  $forgeRunning = ($svc.State -eq 'Running')
  [pscustomobject]@{
    TimestampUtc = [DateTime]::UtcNow.ToString('o')
    TimestampLocal = [DateTimeOffset]::Now.ToString('o')
    Service = $svc
    Processes = $wmiProcesses
    Drivers = $drivers
    Ownership = [pscustomobject]@{
      ForgeActive = $forgeRunning
      MotionAssistantActive = ($motion.Count -gt 0)
      DoubleOwnerDetected = ($forgeRunning -and $motion.Count -gt 0)
    }
    ForgeReadOnlyGet = Get-ReadOnlyApiState -ForgeRunning $forgeRunning
  }
}

function Resolve-CheckedSnapshot {
  if ([string]::IsNullOrWhiteSpace($SnapshotPath)) {
    throw '-SnapshotPath from BackupStatus is required for this action.'
  }
  $resolved = [IO.Path]::GetFullPath($SnapshotPath)
  $prefix = $script:DiagnosticsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "SnapshotPath must stay inside $script:DiagnosticsRoot"
  }
  if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Snapshot not found: $resolved" }
  $resolved
}

function Add-ComparisonRecord {
  param([string]$Path, [object]$Value)
  $Value | ConvertTo-Json -Depth 12 -Compress | Add-Content -LiteralPath $Path -Encoding utf8
}

function Set-ForgeState {
  param([ValidateSet('Running','Stopped')][string]$DesiredState)
  $current = Get-Service -Name $script:ForgeServiceName
  if ($DesiredState -eq 'Stopped' -and $current.Status -ne 'Stopped') {
    Stop-Service -Name $script:ForgeServiceName -NoWait -ErrorAction Stop
    (Get-Service -Name $script:ForgeServiceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
  } elseif ($DesiredState -eq 'Running' -and $current.Status -ne 'Running') {
    Start-Service -Name $script:ForgeServiceName -ErrorAction Stop
    (Get-Service -Name $script:ForgeServiceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
  }
}

if ($Action -eq 'Status') {
  Get-Inventory | ConvertTo-Json -Depth 12
  return
}

if ($Action -eq 'BackupStatus') {
  $inventory = Get-Inventory
  $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
  $SnapshotPath = Join-Path $script:DiagnosticsRoot "vendor-compare-snapshot-$stamp.json"
  $LogPath = Join-Path $script:DiagnosticsRoot "vendor-compare-$stamp.jsonl"
  $snapshot = [pscustomobject]@{
    Format = 1
    CreatedUtc = [DateTime]::UtcNow.ToString('o')
    Machine = $env:COMPUTERNAME
    Port = $Port
    OriginalServiceState = $inventory.Service.State
    Inventory = $inventory
  }
  $snapshot | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $SnapshotPath -Encoding utf8
  Add-ComparisonRecord -Path $LogPath -Value ([pscustomobject]@{ Action='BackupStatus'; Inventory=$inventory })
  Write-Output "Snapshot=$SnapshotPath"
  Write-Output "Log=$LogPath"
  return
}

Assert-Administrator
$SnapshotPath = Resolve-CheckedSnapshot
$snapshot = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json
if ($snapshot.Format -ne 1 -or -not $snapshot.OriginalServiceState) { throw 'Unsupported or incomplete service snapshot.' }
$LogPath = [IO.Path]::ChangeExtension($SnapshotPath,'.jsonl')
$before = Get-Inventory
$operationError = $null
try {
  switch ($Action) {
    'StopForge' {
      Set-ForgeState -DesiredState 'Stopped'
    }
    'StartForge' {
      if ($before.Ownership.MotionAssistantActive) { throw 'Refusing StartForge: MotionAssistant.exe is active; double owner prevention.' }
      Set-ForgeState -DesiredState 'Running'
    }
    'LaunchMA' {
      if ($before.Ownership.ForgeActive) { throw 'Refusing LaunchMA: GPD Forge service is active; stop it first to prevent double ownership.' }
      if ($before.Ownership.MotionAssistantActive) { throw 'Refusing LaunchMA: MotionAssistant.exe is already active.' }
      if (-not (Test-Path -LiteralPath $script:MotionAssistantPath -PathType Leaf)) { throw "Motion Assistant executable not found: $script:MotionAssistantPath" }
      Start-Process -FilePath $script:MotionAssistantPath -WorkingDirectory (Split-Path -Parent $script:MotionAssistantPath) -ErrorAction Stop
      Start-Sleep -Seconds 2
      if (-not (Get-Process -Name 'MotionAssistant' -ErrorAction SilentlyContinue)) { throw 'Motion Assistant process did not appear after launch.' }
    }
    'StopMA' {
      $maProcesses = @(Get-Process -Name 'MotionAssistant' -ErrorAction SilentlyContinue)
      foreach ($proc in $maProcesses) {
        if ($proc.MainWindowHandle -eq 0 -or -not $proc.CloseMainWindow()) {
          throw 'Motion Assistant has no closable main window; no forced process termination was attempted.'
        }
      }
      foreach ($proc in $maProcesses) {
        if (-not $proc.WaitForExit(10000)) { throw 'Motion Assistant did not close within 10 seconds; no forced termination was attempted.' }
      }
    }
    'RestoreService' {
      if ($snapshot.OriginalServiceState -eq 'Running' -and $before.Ownership.MotionAssistantActive) {
        throw 'Refusing RestoreService while MotionAssistant.exe is active. Run StopMA first.'
      }
      $desired = if ($snapshot.OriginalServiceState -eq 'Running') { 'Running' } else { 'Stopped' }
      Set-ForgeState -DesiredState $desired
    }
  }
} catch {
  $operationError = $_.Exception.Message
}
$after = Get-Inventory
$record = [pscustomobject]@{ Action=$Action; SnapshotPath=$SnapshotPath; Before=$before; After=$after; Error=$operationError }
Add-ComparisonRecord -Path $LogPath -Value $record
if ($operationError) { throw $operationError }
$record | ConvertTo-Json -Depth 12
