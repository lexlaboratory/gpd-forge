<#
  GPD Forge - EC read-only readiness probe. GPL-3.0-or-later.

  Fan control talks to the Embedded Controller. Getting a register wrong can brick the EC,
  so this script does NOT read or write the EC. It only:
    1. detects the device model (DMI) to pick the correct register row,
    2. checks that PawnIO (the safe kernel access layer) is present,
    3. prints the plan for the read-only validation that comes next (via the C# broker).

  No hardware access happens here. Safe to run anytime, even with agents working.
#>
$ErrorActionPreference = 'Stop'

Write-Host "GPD Forge - EC readiness (read-only, no EC access)`n" -ForegroundColor Cyan

# 1) Model detection
$prod = (Get-CimInstance Win32_ComputerSystemProduct).Name
$bios = (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion
Write-Host "Device: $prod   BIOS: $bios"

# Per-model EC map (from gpd-fan, GPL-2). VERIFY on hardware before enabling manual fan control.
$map = @{
    'G1618-04' = @{ Name='GPD Win 4'; Cmd='0x4E/0x4F (7840U v1.0); HX370 2025 UNVERIFIED'; EcRam='0x0218 / 0x0478'; Rpm='0x1809 / 0x047A'; Pwm='0x0275 / 0x047A'; Max='184 (verify)' }
}
if ($map.ContainsKey($prod)) {
    $m = $map[$prod]
    Write-Host "`nApplicable EC register row ($($m.Name)):" -ForegroundColor Green
    Write-Host ("  cmd/data ports : {0}" -f $m.Cmd)
    Write-Host ("  EC RAM base    : {0}" -f $m.EcRam)
    Write-Host ("  RPM read       : {0}" -f $m.Rpm)
    Write-Host ("  PWM write      : {0}" -f $m.Pwm)
    Write-Host ("  PWM max        : {0}" -f $m.Max)
    Write-Host "  (Win 4 2025 HX370 EC map is UNVERIFIED - must confirm RPM readback before any write.)" -ForegroundColor Yellow
} else {
    Write-Host "`nNo pre-mapped register row for '$prod'. See docs/hardware/ec-registers.md." -ForegroundColor Yellow
}

# 2) PawnIO presence (safe WinRing0 replacement)
$svc = Get-Service PawnIO -ErrorAction SilentlyContinue
$pawn = (Test-Path 'C:\Program Files\PawnIO') -or ($null -ne $svc)
$svcTxt = if ($svc) { " (service: $($svc.Status))" } else { "" }
Write-Host ("`nPawnIO present: {0}{1}" -f $pawn, $svcTxt) -ForegroundColor $(if($pawn){'Green'}else{'Yellow'})

# 3) Plan
Write-Host "`nNext (read-only, gated):" -ForegroundColor Cyan
Write-Host "  1. Implement core/Broker on PawnIO (ecRead only, whitelisted to this model registers)."
Write-Host "  2. Read the RPM register and confirm it tracks the fan (observe only, NO writes)."
Write-Host "  3. Only after the map is confirmed on THIS device: enable manual PWM behind approval."
Write-Host "`nNothing was read from or written to the EC by this script." -ForegroundColor DarkGray
