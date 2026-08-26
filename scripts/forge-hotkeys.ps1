# GPD Forge — resident global hotkeys. GPL-3.0-or-later.
#
# Registers global hotkeys that drive the daemon's local API (127.0.0.1:8787), so you can bump TDP
# or switch mode from inside any game without opening the UI. Hosted by the signed powershell.exe,
# so it runs under Smart App Control (no unsigned binary of ours). The daemon runs in session 0 and
# cannot register user-session hotkeys itself — this helper does, and forwards to the API.
#
# Defaults:  Ctrl+Alt+Up = TDP +2W   Ctrl+Alt+Down = TDP -2W   Ctrl+Alt+M = cycle mode
#
# Test the mechanism without going resident:
#   powershell -ExecutionPolicy Bypass -File scripts\forge-hotkeys.ps1 -SelfTest
# Run resident (leave it running):
#   powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File scripts\forge-hotkeys.ps1
param(
  [string]$Api = 'http://127.0.0.1:8787',
  [int]$TdpStep = 2,
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

if ($SelfTest) { $form.Clear(); $form.Dispose(); Write-Output "SELFTEST_OK hotkeys=3"; exit 0 }

$script:tdp = 20
$modes = @('windows', 'gaming', 'ai', 'battery')
function Post($path, $body) { try { Invoke-RestMethod "$Api$path" -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 4 | Out-Null } catch {} }
function Get-Mode { try { return (Invoke-RestMethod "$Api/mode" -TimeoutSec 4).active } catch { return 'windows' } }

$handler = {
  param($id)
  switch ($id) {
    1 { $script:tdp = [Math]::Min(40, $script:tdp + $TdpStep); Post '/tdp' @{ stapmW = $script:tdp } }
    2 { $script:tdp = [Math]::Max(5,  $script:tdp - $TdpStep); Post '/tdp' @{ stapmW = $script:tdp } }
    3 { $cur = Get-Mode; $i = [Math]::Max(0, [Array]::IndexOf($modes, $cur)); $next = $modes[($i + 1) % $modes.Count]; Post '/mode' @{ name = $next } }
  }
}
$form.add_Hit($handler)
Write-Output "GPD Forge hotkeys armed -> $Api  (Ctrl+Alt+Up/Down = TDP, Ctrl+Alt+M = mode). Ctrl+C to stop."
[System.Windows.Forms.Application]::Run()
$form.Clear()
