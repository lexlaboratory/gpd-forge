# GPD Forge - session tray + local alert bridge. GPL-3.0-or-later.
#
# Runs in the user's session because the daemon runs as LocalSystem in session 0 and cannot show a
# tray icon or a notification there. Hosted by the signed powershell.exe, so it runs under Smart App
# Control without shipping an unsigned binary of our own.
#
# The tray is the app's smallest surface, so it carries the same rule as the rest of the redesign:
# it shows what is measured and nothing else.
param(
  [string]$Api = 'http://127.0.0.1:8787',
  [int]$IntervalSec = 5,
  [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$mutex = New-Object System.Threading.Mutex($false, 'Global\GPDForge.Tray')
if (-not $mutex.WaitOne(0, $false)) { exit 0 }

$appExe = Join-Path $env:ProgramFiles 'GPD Forge\GPD Forge.exe'

# The desktop shell cannot be handed a route, so a deep link has to go through the browser, which
# the daemon serves the same UI to.
function Open-Forge {
  param([string]$Hash = '')
  if ($Hash) { Start-Process "$Api/#$Hash"; return }
  if (Test-Path $appExe) { Start-Process $appExe } else { Start-Process $Api }
}

$ni = New-Object System.Windows.Forms.NotifyIcon
$iconPath = Join-Path $env:ProgramFiles 'GPD Forge\icon.ico'
if (Test-Path $iconPath) { $ni.Icon = New-Object System.Drawing.Icon($iconPath) }
else { $ni.Icon = [System.Drawing.SystemIcons]::Information }
$ni.Text = 'GPD Forge'
$ni.Visible = $true

$menu = New-Object System.Windows.Forms.ContextMenuStrip
($menu.Items.Add('Open GPD Forge')).Add_Click({ Open-Forge })
($menu.Items.Add('Alerts')).Add_Click({ Open-Forge -Hash 'alerts' })
$menu.Items.Add('-') | Out-Null
# Panic cool is here because the moment you want it is the moment you do not want to go looking for
# a window: floor the TDP and max the fan from the tray, in one click.
($menu.Items.Add('Panic cool')).Add_Click({
  try {
    $r = Invoke-RestMethod "$Api/panic" -Method Post -TimeoutSec 8
    $msg = if ($r.applied) { "Floored to $($r.stapmW) W, fan Aggressive." } else { "Requested $($r.stapmW) W floor - not verified." }
    $icon = if ($r.applied) { [System.Windows.Forms.ToolTipIcon]::Info } else { [System.Windows.Forms.ToolTipIcon]::Warning }
    $ni.ShowBalloonTip(4000, 'GPD Forge - Panic cool', $msg, $icon)
  } catch {
    $ni.ShowBalloonTip(4000, 'GPD Forge', 'Panic cool failed - the daemon did not answer.', [System.Windows.Forms.ToolTipIcon]::Error)
  }
})
$menu.Items.Add('-') | Out-Null
($menu.Items.Add('Exit tray icon')).Add_Click({ $script:stop = $true })
$ni.ContextMenuStrip = $menu
$ni.Add_DoubleClick({ Open-Forge })
$ni.Add_BalloonTipClicked({ Open-Forge -Hash 'alerts' })

if ($SelfTest) {
  $ni.ShowBalloonTip(1500, 'GPD Forge', 'Tray self-test OK', [System.Windows.Forms.ToolTipIcon]::Info)
  Start-Sleep -Milliseconds 300
  $ni.Visible = $false; $ni.Dispose(); $mutex.ReleaseMutex(); $mutex.Dispose()
  Write-Output 'SELFTEST_OK tray'
  exit 0
}

$lastAlertId = $null
$lastAlertCount = 0
$script:stop = $false

try {
  while (-not $script:stop) {
    [System.Windows.Forms.Application]::DoEvents()

    # Live hover text. A tray icon that says the same sentence forever tells you nothing; this one
    # answers "is it hot, and what mode am I in?" without opening anything. Capped at 63 chars
    # because NotifyIcon.Text silently throws above that.
    try {
      $t = Invoke-RestMethod "$Api/telemetry" -TimeoutSec 3
      $m = Invoke-RestMethod "$Api/mode" -TimeoutSec 3
      $fps = if ($t.fps -gt 0) { " | $([Math]::Round($t.fps)) fps" } else { '' }
      $text = "GPD Forge | $($m.active) | $([Math]::Round($t.cpuTempC))C | $([Math]::Round($t.packageW))W$fps"
      $ni.Text = if ($text.Length -gt 63) { $text.Substring(0, 63) } else { $text }
    } catch {
      $ni.Text = 'GPD Forge - daemon not answering'
    }

    try {
      $summary = Invoke-RestMethod "$Api/alerts/summary" -TimeoutSec 4
      $a = $summary.latest
      if ($a -and -not $a.acknowledged -and $a.severity -ne 'Info') {
        # The daemon coalesces a continuous condition into ONE alert whose count climbs, so
        # notifying on a changed id alone would stay silent through an escalating fault, while
        # notifying on every count bump would be the 62-popup version of the same bug. Notify on a
        # new alert, and again only if it has fired substantially more since we last spoke up.
        $count = if ($null -ne $a.count) { [int]$a.count } else { 1 }
        $isNew = $a.id -ne $lastAlertId
        $hasEscalated = (-not $isNew) -and ($count -ge $lastAlertCount * 4) -and ($count -gt $lastAlertCount)
        if ($isNew -or $hasEscalated) {
          $lastAlertId = $a.id
          $lastAlertCount = $count
          $times = if ($count -gt 1) { " (x$count)" } else { '' }
          $kind = if ($a.severity -eq 'Critica') { [System.Windows.Forms.ToolTipIcon]::Error }
                  else { [System.Windows.Forms.ToolTipIcon]::Warning }
          $ni.ShowBalloonTip(6500, "GPD Forge | $($a.title)$times", $a.message, $kind)
        }
      }
    } catch { }

    Start-Sleep -Seconds ([Math]::Max(1, $IntervalSec))
  }
} finally {
  $ni.Visible = $false; $ni.Dispose(); $mutex.ReleaseMutex(); $mutex.Dispose()
}
