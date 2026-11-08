#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(2, 120)][int]$Seconds = 30,
    [switch]$NoBuild
)

. (Join-Path $PSScriptRoot 'common.ps1')
Push-Location $repoRoot
$previousSeconds = $env:SIDECUE_AUDIO_PROBE_SECONDS
try {
    Assert-SidecueSdk
    if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
    $probeDirectory = Join-Path $repoRoot 'artifacts\audio-recovery-probe'
    New-Item -ItemType Directory -Force -Path $probeDirectory | Out-Null
    $applicationPath = Join-Path $repoRoot "src\Sidecue.Desktop\bin\$Configuration\net10.0-windows10.0.26100.0\Sidecue.Desktop.exe"
    $env:SIDECUE_AUDIO_PROBE_SECONDS = $Seconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    Write-Host "Capturing metadata for $Seconds seconds. Perform the planned endpoint changes during this window."
    $process = Start-Process -FilePath $applicationPath -ArgumentList @('--probe-audio-recovery', ('"' + $probeDirectory + '"')) -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(($Seconds + 20) * 1000)) {
        $process.Kill()
        throw 'The recovery probe exceeded its time limit.'
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "The recovery probe failed with exit code $($process.ExitCode). Inspect $probeDirectory\audio-recovery-probe.json." }
    Get-Content -LiteralPath (Join-Path $probeDirectory 'audio-recovery-probe.json')
} finally {
    $env:SIDECUE_AUDIO_PROBE_SECONDS = $previousSeconds
    Pop-Location
}
