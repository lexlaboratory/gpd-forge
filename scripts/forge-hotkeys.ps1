# GPD Forge — resident global hotkeys. GPL-3.0-or-later.
#
# Registers global hotkeys that drive the daemon's local API (127.0.0.1:8787), so you can bump TDP
# or switch mode from inside any game without opening the UI. Hosted by the signed powershell.exe,
# so it runs under Smart App Control (no unsigned binary of ours). The daemon runs in session 0 and
# cannot register user-session hotkeys itself — this helper does, and forwards to the API.
#
# Defaults:  Ctrl+Alt+Up = TDP +2W   Ctrl+Alt+Down = TDP -2W   Ctrl+Alt+M = cycle mode
#
# Steps are relative to the daemon's real TDP (read from GET /mode + GET /profiles), re-read on the
# first press and whenever the cached value is older than -TdpResyncSeconds.
#
# Test the mechanism without going resident:
#   powershell -ExecutionPolicy Bypass -File scripts\forge-hotkeys.ps1 -SelfTest
# Run resident (leave it running):
#   powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File scripts\forge-hotkeys.ps1
param(
  [string]$Api = 'http://127.0.0.1:8787',
  [int]$TdpStep = 2,
  [int]$TdpResyncSeconds = 10,
  [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$src = @"
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Runtime.InteropServices;
public class ForgeHotkeys : Form {
  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
  const int WM_HOTKEY = 0x0312;
  public event Action<int> Hit;
  readonly List<int> _ids = new List<int>();
  public bool Add(int id, uint mod, uint vk) { _ids.Add(id); return RegisterHotKey(this.Handle, id, mod, vk); }
  public void Clear() { foreach (var id in _ids) UnregisterHotKey(this.Handle, id); _ids.Clear(); }
  protected override void WndProc(ref Message m) { if (m.Msg == WM_HOTKEY && Hit != null) Hit((int)m.WParam); base.WndProc(ref m); }
  protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }
}
"@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Windows.Forms, System.Drawing

$MOD_ALT = 1; $MOD_CTRL = 2
$VK_UP = 0x26; $VK_DOWN = 0x28; $VK_M = 0x4D
$form = New-Object ForgeHotkeys
$null = $form.Handle
$ok = $true
$ok = $form.Add(1, ($MOD_CTRL -bor $MOD_ALT), $VK_UP)   -and $ok   # TDP up
$ok = $form.Add(2, ($MOD_CTRL -bor $MOD_ALT), $VK_DOWN) -and $ok   # TDP down
$ok = $form.Add(3, ($MOD_CTRL -bor $MOD_ALT), $VK_M)    -and $ok   # cycle mode
if (-not $ok) { Write-Error "RegisterHotKey failed (a chord is already taken)."; exit 2 }

$TDP_MIN = 5; $TDP_MAX = 40; $TDP_FALLBACK = 20
$script:tdp = $null              # unknown until the daemon tells us; never assume a starting wattage
$script:tdpAt = [datetime]::MinValue
$modes = @('windows', 'gaming', 'ai', 'battery')

function Post($path, $body) { try { return Invoke-RestMethod "$Api$path" -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 4 } catch { return $null } }
function Get-Mode { try { return (Invoke-RestMethod "$Api/mode" -TimeoutSec 4).active } catch { return 'windows' } }

# The daemon has no GET /tdp, but the active mode's stored preset is the wattage it last applied, so
# mode + profiles is the authoritative answer for "what is the TDP right now".
function Get-DaemonTdp {
  try {
    $active = (Invoke-RestMethod "$Api/mode" -TimeoutSec 4).active
    if (-not $active) { return $null }
    $w = (Invoke-RestMethod "$Api/profiles" -TimeoutSec 4).$active.stapmW
    if ($null -eq $w) { return $null }
    return [int]$w
  } catch { return $null }
}

# Resolves the wattage a step should start from. The old code seeded 20 W once and never looked
# again, so the very first Ctrl+Alt+Up jumped to 22 W regardless of the real TDP. We re-read on the
# first press and whenever our cached value is stale, which also picks up TDP changes made in the UI
# between presses.
function Get-CurrentTdp {
  $stale = ((Get-Date) - $script:tdpAt).TotalSeconds -ge $TdpResyncSeconds
  if ($null -eq $script:tdp -or $stale) {
    $fresh = Get-DaemonTdp
    if ($null -ne $fresh) { $script:tdp = $fresh; $script:tdpAt = Get-Date }
  }
  if ($null -eq $script:tdp) { return $TDP_FALLBACK }   # daemon unreachable: last resort, not a seed
  return [int]$script:tdp
}

function Set-Tdp($watts) {
  $clamped = [Math]::Min($TDP_MAX, [Math]::Max($TDP_MIN, [int]$watts))
  $r = Post '/tdp' @{ stapmW = $clamped }
  # Trust what the hardware actually held over what we asked for, so the next step is not built on a
  # wattage the controller refused.
  if ($null -ne $r -and $null -ne $r.observed) { $script:tdp = [int]$r.observed } else { $script:tdp = $clamped }
  $script:tdpAt = Get-Date
  return $script:tdp
}

if ($SelfTest) {
  $form.Clear(); $form.Dispose()
  $probe = Get-CurrentTdp                       # must never throw, daemon up or down
  $daemon = Get-DaemonTdp
  $source = if ($null -eq $daemon) { 'fallback' } else { 'daemon' }
  if ($probe -lt $TDP_MIN -or $probe -gt $TDP_MAX) { Write-Error "Resolved TDP $probe out of range."; exit 3 }
  Write-Output "SELFTEST_OK hotkeys=3 tdp=$probe source=$source step=$TdpStep"
  exit 0
}

$handler = {
  param($id)
  switch ($id) {
    1 { $null = Set-Tdp ((Get-CurrentTdp) + $TdpStep) }
    2 { $null = Set-Tdp ((Get-CurrentTdp) - $TdpStep) }
    3 {
      $cur = Get-Mode; $i = [Math]::Max(0, [Array]::IndexOf($modes, $cur)); $next = $modes[($i + 1) % $modes.Count]
      $null = Post '/mode' @{ name = $next }
      $script:tdpAt = [datetime]::MinValue   # a mode switch reapplies its own preset: our cache is void
    }
  }
}
$form.add_Hit($handler)
Write-Output "GPD Forge hotkeys armed -> $Api  (Ctrl+Alt+Up/Down = TDP, Ctrl+Alt+M = mode). Ctrl+C to stop."
[System.Windows.Forms.Application]::Run()
$form.Clear()
