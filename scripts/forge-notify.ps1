# GPD Forge — resident alert notifier. GPL-3.0-or-later.
#
# Polls the daemon's guardian and raises a Windows notification on a NEW warn/critical alert
# (overheat, low battery). Session-0 services can't show user toasts, so this user-session helper
# does — hosted by the signed powershell.exe, so it's fine under Smart App Control.
#
#   powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File scripts\forge-notify.ps1
param(
  [string]$Api = 'http://127.0.0.1:8787',
  [int]$IntervalSec = 5,
  [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$ni = New-Object System.Windows.Forms.NotifyIcon
$ni.Icon = [System.Drawing.SystemIcons]::Information
$ni.Text = 'GPD Forge'
$ni.Visible = $true

if ($SelfTest) {
  $ni.ShowBalloonTip(1500, 'GPD Forge', 'Notifier self-test OK', [System.Windows.Forms.ToolTipIcon]::Info)
  Start-Sleep -Milliseconds 300
  $ni.Visible = $false; $ni.Dispose()
  Write-Output 'SELFTEST_OK notifier'
  exit 0
}

$last = $null
Write-Output "GPD Forge notifier watching $Api/guardian every ${IntervalSec}s. Ctrl+C to stop."
try {
  while ($true) {
    try {
      $g = Invoke-RestMethod "$Api/guardian" -TimeoutSec 4
      if ($g.lastAlert -and $g.lastAlert -ne $last -and ($g.lastSeverity -eq 'warn' -or $g.lastSeverity -eq 'critical')) {
        $last = $g.lastAlert
        $icon = if ($g.lastSeverity -eq 'critical') { [System.Windows.Forms.ToolTipIcon]::Error } else { [System.Windows.Forms.ToolTipIcon]::Warning }
        $ni.ShowBalloonTip(6000, "GPD Forge - $($g.lastSeverity)", $g.lastAlert, $icon)
      }
    } catch { }
    Start-Sleep -Seconds $IntervalSec
  }
} finally { $ni.Visible = $false; $ni.Dispose() }
