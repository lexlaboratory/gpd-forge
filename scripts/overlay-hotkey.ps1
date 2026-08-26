# GPD Forge — resident overlay hotkey listener. GPL-3.0-or-later.
#
# Registers a GLOBAL hotkey and toggles the Quick Access Menu overlay on/off.
# Runs under Smart App Control because it's hosted by the Microsoft-signed powershell.exe
# (no unsigned binary of ours). Bind your GPD "Home" button to this hotkey by mapping a back
# paddle (L4/R4) or Menu to the same chord with WinControls (see scripts/gpd-winctl.ps1).
#
# Test the mechanism without going resident:
#   powershell -ExecutionPolicy Bypass -File scripts\overlay-hotkey.ps1 -SelfTest
# Run resident (leave it running; press Ctrl+Alt+Home to toggle):
#   powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File scripts\overlay-hotkey.ps1
param(
  [string]$Modifiers = "Ctrl,Alt",     # any of Ctrl,Alt,Shift,Win (comma-separated); empty for none
  [string]$Key = "Home",               # a VK name: Home, F24, Insert, etc.
  [string]$Url = "http://127.0.0.1:8787/overlay.html",
  [int]$Width = 380,
  [switch]$SelfTest
)
$ErrorActionPreference = "Stop"

# --- resolve modifiers + virtual-key code (note: PowerShell variable names are case-insensitive,
#     so the modifier map and the accumulator must have distinct names) ---
$modMap = @{ ALT = 1; CONTROL = 2; CTRL = 2; SHIFT = 4; WIN = 8 }
$modBits = 0
foreach ($m in ($Modifiers -split ',')) { $t = $m.Trim().ToUpper(); if ($t -and $modMap.ContainsKey($t)) { $modBits = $modBits -bor $modMap[$t] } }
Add-Type -AssemblyName System.Windows.Forms
try { $vk = [int][Enum]::Parse([System.Windows.Forms.Keys], $Key, $true) } catch { Write-Error "Unknown key '$Key'"; exit 1 }
if ($vk -le 0) { Write-Error "Unknown key '$Key'"; exit 1 }

Add-Type -AssemblyName System.Drawing
$src = @"
using System;
using System.Windows.Forms;
using System.Runtime.InteropServices;
public class ForgeHotkey : Form {
  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
  const int WM_HOTKEY = 0x0312; const int ID = 0xB0F;
  public event Action Hit;
  uint _mod, _vk;
  public ForgeHotkey(uint mod, uint vk){ _mod = mod; _vk = vk; }
  public bool Arm(){ return RegisterHotKey(this.Handle, ID, _mod, _vk); }
  public void Disarm(){ UnregisterHotKey(this.Handle, ID); }
  protected override void WndProc(ref Message m){ if (m.Msg == WM_HOTKEY && Hit != null) Hit(); base.WndProc(ref m); }
  protected override void SetVisibleCore(bool value){ base.SetVisibleCore(false); } // never show
}
"@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Windows.Forms, System.Drawing

$form = New-Object ForgeHotkey([uint32]$modBits, [uint32]$vk)
$null = $form.Handle  # force handle creation so RegisterHotKey has a window
if (-not $form.Arm()) { Write-Error "RegisterHotKey failed (chord already taken?). mod=$modBits vk=$vk"; exit 2 }

if ($SelfTest) {
  $form.Disarm(); $form.Dispose()
  Write-Output "SELFTEST_OK mod=$modBits vk=$vk key=$Key"
  exit 0
}

# Toggle: launch the overlay app-window if closed, else close it.
$script:proc = $null
$launch = Join-Path $PSScriptRoot "overlay-launch.ps1"
$toggle = {
  if ($script:proc -and -not $script:proc.HasExited) {
    try { $script:proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 200; if (-not $script:proc.HasExited) { $script:proc.Kill() } } catch {}
    $script:proc = $null
  } else {
    $script:proc = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList @(
      "-ExecutionPolicy", "Bypass", "-File", $launch, "-Url", $Url, "-Width", $Width
    )
  }
}
$form.add_Hit($toggle)

Write-Output "GPD Forge overlay hotkey armed: $Modifiers+$Key -> $Url  (Ctrl+C to stop)"
[System.Windows.Forms.Application]::Run()
$form.Disarm()
