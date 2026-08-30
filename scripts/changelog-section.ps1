# GPD Forge - extract ONE version's section from CHANGELOG.md. GPL-3.0-or-later.
#
# release.yml used `body_path: CHANGELOG.md`, so the v0.2.0 release was published with the entire
# file as its body: the "# Changelog" header, the empty [Unreleased] section, and every previous
# version's notes. Nobody noticed until it was already public, because the workflow fires on the tag
# push and finishes before a human looks.
#
# This extracts the requested version's section and nothing else.
#
# It FAILS when the version is not found rather than falling back to the whole file. A fallback would
# reproduce the exact bug this exists to fix, and silently: the release would look fine to the
# workflow and wrong to everyone reading it. A missing section means the changelog was not updated
# for this release, which is worth stopping for.
#
# ASCII only, deliberately. Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so an em dash in
# this file arrives as mojibake and takes the parser with it - which is exactly how the first version
# of this script failed.
#
#   ./scripts/changelog-section.ps1 -Version 0.2.0                 # print the section
#   ./scripts/changelog-section.ps1 -Version 0.2.0 -OutFile n.md   # write it
#   ./scripts/changelog-section.ps1 -SelfTest                      # check the parser
param(
    [string]$Version,
    [string]$Path = "$PSScriptRoot\..\CHANGELOG.md",
    [string]$OutFile,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'

function Get-ChangelogSection {
    param([string]$Text, [string]$Version)

    $lines = $Text -split "`r?`n"

    # Built by concatenation rather than interpolation: "$|" inside a double-quoted string is parsed
    # as a variable and mangles the pattern. Anchored on the bracketed version so that looking for
    # 0.2.1 cannot match a heading for 0.2.10.
    $escaped = [regex]::Escape($Version)
    $bracketed = '^##\s+\[' + $escaped + '\]'
    $bare = '^##\s+' + $escaped + '(\s|$)'

    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $bracketed -or $lines[$i] -match $bare) { $start = $i; break }
    }
    if ($start -lt 0) { return $null }

    # Ends at the next level-2 heading - the next version, or [Unreleased] if the file is ordered
    # oddly. Level-3 headings (### Added) belong to this section and must not end it.
    $end = $lines.Count
    for ($j = $start + 1; $j -lt $lines.Count; $j++) {
        if ($lines[$j] -match '^##\s') { $end = $j; break }
    }

    # Drop the heading itself: the GitHub release already shows the version as its title, so
    # repeating it in the body is noise.
    $body = $lines[($start + 1)..($end - 1)]
    return ($body -join "`n").Trim()
}

if ($SelfTest) {
    $sample = @'
# Changelog

Some preamble nobody wants in a release body.

## [Unreleased]

## [0.2.0] - 2026-08-30

Real notes for 0.2.0.

### Added
- A thing.

## [0.1.0] - 2026-08-01

Older notes that must not appear.
'@

    $got = Get-ChangelogSection -Text $sample -Version '0.2.0'
    $fail = @()
    if ($got -notmatch 'Real notes for 0\.2\.0') { $fail += 'missing its own notes' }
    if ($got -match 'Older notes')               { $fail += 'leaked the previous version' }
    if ($got -match 'preamble')                  { $fail += 'included the file header' }
    if ($got -match '\[Unreleased\]')            { $fail += 'included Unreleased' }
    if ($got -notmatch '### Added')              { $fail += 'dropped its own subsection' }
    if ($got -match '(?m)^##\s')                 { $fail += 'kept a level-2 heading' }

    # A version that is not there must yield nothing, so the caller can fail loudly.
    if ($null -ne (Get-ChangelogSection -Text $sample -Version '9.9.9')) { $fail += 'invented a missing version' }

    if ($fail.Count -gt 0) { Write-Host "SELFTEST_FAIL: $($fail -join '; ')" -ForegroundColor Red; exit 1 }
    Write-Host "SELFTEST_OK extracted=$($got.Length) chars"
    exit 0
}

if (-not $Version) { Write-Host "Specify -Version (or -SelfTest)." -ForegroundColor Red; exit 2 }

$text = Get-Content -Raw -Path $Path
$section = Get-ChangelogSection -Text $text -Version $Version

if ([string]::IsNullOrWhiteSpace($section)) {
    Write-Host "No section for version '$Version' in $Path." -ForegroundColor Red
    Write-Host "Refusing to fall back to the whole file - that is the bug this script exists to fix." -ForegroundColor Red
    exit 1
}

if ($OutFile) {
    # UTF8 without a BOM: a BOM renders as a stray glyph at the top of the release body on GitHub.
    [IO.File]::WriteAllText($OutFile, $section, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Wrote $($section.Length) chars to $OutFile"
} else {
    Write-Output $section
}
