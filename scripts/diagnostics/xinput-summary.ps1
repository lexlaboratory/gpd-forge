function Get-XInputTriggerAssessment {
  [CmdletBinding()]
  param([Parameter(Mandatory)][AllowEmptyCollection()][int[]]$Samples)
  if ($Samples.Count -eq 0) {
    return [pscustomobject]@{ Min=$null; Max=$null; Reached255=$false; HeldAtLeast250=$false; ReleasedTo5AfterPeak=$false; CriteriaMet=$false }
  }
  $min = ($Samples | Measure-Object -Minimum).Minimum
  $max = ($Samples | Measure-Object -Maximum).Maximum
  $peakIndex = [Array]::IndexOf($Samples, 255)
  $heldIndex = -1
  if ($peakIndex -ge 0) {
    for ($i=$peakIndex; $i -lt ($Samples.Count - 1); $i++) {
      if ($Samples[$i] -ge 250 -and $Samples[$i + 1] -ge 250) { $heldIndex=$i; break }
    }
  }
  $released = $false
  if ($heldIndex -ge 0) {
    for ($i=$heldIndex + 2; $i -lt $Samples.Count; $i++) {
      if ($Samples[$i] -le 5) { $released=$true; break }
    }
  }
  $reached = ($peakIndex -ge 0)
  $held = ($heldIndex -ge 0)
  return [pscustomobject]@{ Min=[int]$min; Max=[int]$max; Reached255=$reached; HeldAtLeast250=$held; ReleasedTo5AfterPeak=$released; CriteriaMet=($reached -and $held -and $released) }
}

