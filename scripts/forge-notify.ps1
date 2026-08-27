# GPD Forge — premium session tray + local alert bridge. GPL-3.0-or-later.
param([string]$Api = 'http://127.0.0.1:8787', [int]$IntervalSec = 5, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$mutex = New-Object System.Threading.Mutex($false, 'Global\GPDForge.Tray')
if (-not $mutex.WaitOne(0, $false)) { exit 0 }
$open = { Start-Process $Api }
$ni = New-Object System.Windows.Forms.NotifyIcon
$iconPath = Join-Path $env:ProgramFiles 'GPD Forge\icon.ico'
if (Test-Path $iconPath) { $ni.Icon = New-Object System.Drawing.Icon($iconPath) } else { $ni.Icon = [System.Drawing.SystemIcons]::Information }
$ni.Text = 'GPD Forge — local performance control'; $ni.Visible = $true
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$openItem = $menu.Items.Add('Open GPD Forge'); $openItem.Add_Click($open)
$statusItem = $menu.Items.Add('Service status')
$statusItem.Add_Click({ try { $h = Invoke-RestMethod "$Api/health" -TimeoutSec 3; $ni.ShowBalloonTip(3500, 'GPD Forge', "Online · $($h.model)", [System.Windows.Forms.ToolTipIcon]::Info) } catch { $ni.ShowBalloonTip(3500, 'GPD Forge', 'Service offline', [System.Windows.Forms.ToolTipIcon]::Warning) } })
$menu.Items.Add('-') | Out-Null
$exitItem = $menu.Items.Add('Exit tray icon'); $exitItem.Add_Click({ $script:stop = $true })
$ni.ContextMenuStrip = $menu; $ni.Add_DoubleClick($open)
$ni.Add_BalloonTipClicked({ Start-Process "$Api/#alerts" })
if ($SelfTest) { $ni.ShowBalloonTip(1500, 'GPD Forge', 'Tray self-test OK', [System.Windows.Forms.ToolTipIcon]::Info); Start-Sleep -Milliseconds 300; $ni.Visible = $false; $ni.Dispose(); $mutex.ReleaseMutex(); $mutex.Dispose(); Write-Output 'SELFTEST_OK tray'; exit 0 }
$lastAlert = $null; $script:stop = $false
try {
  while (-not $script:stop) {
    [System.Windows.Forms.Application]::DoEvents()
    try {
      $summary = Invoke-RestMethod "$Api/alerts/summary" -TimeoutSec 4
      if ($summary.latest -and -not $summary.latest.acknowledged -and $summary.latest.id -ne $lastAlert) {
        $lastAlert = $summary.latest.id
        if ($summary.latest.severity -ne 'Info') {
          $kind = if ($summary.latest.severity -eq 'Critica') { [System.Windows.Forms.ToolTipIcon]::Error } else { [System.Windows.Forms.ToolTipIcon]::Warning }
          $ni.ShowBalloonTip(6500, "GPD Forge · $($summary.latest.title)", $summary.latest.message, $kind)
        }
      }
    } catch { }
    Start-Sleep -Seconds ([Math]::Max(1, $IntervalSec))
  }
} finally { $ni.Visible = $false; $ni.Dispose(); $mutex.ReleaseMutex(); $mutex.Dispose() }
