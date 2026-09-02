<#
  GPD Forge - post-install verification. GPL-3.0-or-later.

  Checks the seam the test suite structurally cannot reach: the Playwright E2E run targets the mock
  daemon and a `vite preview` server, so it never exercises the real C# daemon nor the packaged Tauri
  shell. On 2026-08-27 that blind spot let a shell binary older than the UI it embedded reach
  Program Files; every dashboard tile showed "--" while the suite stayed green.

  Exits 0 if the installation is sound, 1 otherwise. Read-only: it never changes power or fan state.

  Usage:
    powershell -ExecutionPolicy Bypass -File scripts\verify-install.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Program Files\GPD Forge',
    [string]$Url = 'http://127.0.0.1:8787',
    [string]$ServiceName = 'GPDForge'
)

$failures = @()

function Test-Check {
    param([string]$Name, [scriptblock]$Body)
    try {
        $detail = & $Body
        Write-Host ("  [ok]   {0}{1}" -f $Name, $(if ($detail) { " - $detail" } else { '' })) -ForegroundColor Green
    } catch {
        Write-Host ("  [FAIL] {0} - {1}" -f $Name, $_.Exception.Message) -ForegroundColor Red
        $script:failures += $Name
    }
}

Write-Host "Verifying GPD Forge installation" -ForegroundColor Cyan

# 1) the daemon is registered and running
Test-Check 'service is running' {
    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { throw "service '$ServiceName' is not registered" }
    if ($svc.Status -ne 'Running') { throw "service status is $($svc.Status)" }
    "status=$($svc.Status) start=$($svc.StartType)"
}

# 2) the API answers and telemetry carries real numbers, not a zeroed stub
Test-Check 'telemetry endpoint returns live values' {
    $t = Invoke-RestMethod "$Url/telemetry" -TimeoutSec 8
    if ($null -eq $t.cpuTempC) { throw 'response has no cpuTempC field' }
    if ($t.cpuTempC -le 0) { throw "cpuTempC is $($t.cpuTempC) - sensors are not reporting" }
    if ($null -eq $t.batteryPct) { throw 'response has no batteryPct field' }
    # Sensors report null when they cannot be read (see docs/api.md GET /telemetry). PowerShell
    # renders null as an empty string, so the line came out as "fps=" — which reads like a broken
    # script rather than an unmeasured sensor. Same rule as everywhere else: say "n/a", never invent.
    $show = { param($v) if ($null -eq $v) { 'n/a' } else { $v } }
    "cpu=$(& $show $t.cpuTempC)C pkg=$(& $show $t.packageW)W fan=$(& $show $t.fanRpm)rpm " +
    "batt=$(& $show $t.batteryPct)% fps=$(& $show $t.fps)"
}

# 3) the installed shell is a build that knows how to reach the daemon.
#    Both markers only exist from commit 1437cee onwards: the daemon bring-up path (which names the
#    service DLL) and the window-title stamp. A shell without them embeds the old bundle whose API
#    base was relative, so every fetch resolved against http://tauri.localhost and 404'd.
Test-Check 'installed shell binary is current' {
    $exe = Join-Path $InstallDir 'GPD Forge.exe'
    if (-not (Test-Path $exe)) { throw "missing $exe" }
    $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($exe))
    $missing = @('GpdForge.Service.dll', 'daemon=up') | Where-Object { -not $text.Contains($_) }
    if ($missing) { throw "shell is stale - missing marker(s): $($missing -join ', ')" }
    "built $((Get-Item $exe).LastWriteTime)"
}

# 3b) the INSTALLED daemon serialises enums as names, not ordinals.
#
#     Deliberately not a second copy of the contract validator (that lives in
#     core.tests/ApiContractTests.cs and tests/e2e/contract.spec.ts, both reading
#     tests/contract/api-contract.json). A third implementation in PowerShell would be one more thing
#     to drift. This checks the single failure that has actually shipped: on 2026-08-28 severity came
#     off the wire as 1 instead of "Aviso", the UI called .toLowerCase() on a number, React unmounted
#     and the window went black — with the daemon healthy and every test green the whole time.
#
#     It runs here because the two contract guards test a daemon built from the tree. This one tests
#     the binary in Program Files, which is a different artefact and the one the user runs.
Test-Check 'installed daemon emits enum names, not ordinals' {
    $a = Invoke-RestMethod "$Url/alerts" -TimeoutSec 8
    if ($null -eq $a.alerts) { throw '/alerts response has no alerts array' }
    if ($a.alerts.Count -eq 0) { return 'no alerts recorded yet - nothing to check' }

    $first = $a.alerts[0]
    foreach ($field in 'severity', 'category') {
        $value = $first.$field
        if ($null -eq $value) { throw "alert has no $field field" }
        # PowerShell deserialises a JSON number to Int64/Double and a string to String. Anything
        # numeric here means JsonStringEnumConverter is missing from Program.cs.
        if ($value -isnot [string]) {
            throw ("alert.$field is $($value.GetType().Name) '$value', not a string - the " +
                   'JsonStringEnumConverter is missing and the Alerts page will unmount React')
        }
    }
    "severity=$($first.severity) category=$($first.category) over $($a.alerts.Count) alert(s)"
}

# 4) the served dashboard points at assets that actually exist.
#    wwwroot used to accumulate every past build, so a dangling <script src> was easy to miss.
Test-Check 'wwwroot index references assets that exist' {
    $index = Join-Path $InstallDir 'service\wwwroot\index.html'
    if (-not (Test-Path $index)) { throw "missing $index" }
    $html = Get-Content $index -Raw
    $refs = [regex]::Matches($html, '(?:src|href)="(/assets/[^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    if (-not $refs) { throw 'index.html references no /assets bundle' }
    $dangling = $refs | Where-Object { -not (Test-Path (Join-Path "$InstallDir\service\wwwroot" $_.TrimStart('/'))) }
    if ($dangling) { throw "dangling asset reference(s): $($dangling -join ', ')" }
    "$($refs.Count) asset(s) resolved"
}

if ($failures.Count -gt 0) {
    Write-Host ("`nVerification FAILED ({0}): {1}" -f $failures.Count, ($failures -join '; ')) -ForegroundColor Red
    exit 1
}
Write-Host "`nVerification passed - the installation is sound." -ForegroundColor Green
exit 0
