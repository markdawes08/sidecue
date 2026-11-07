#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild
)

. (Join-Path $PSScriptRoot 'common.ps1')
Push-Location $repoRoot
try {
    Assert-SidecueSdk
    if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
    $probeDirectory = Join-Path $repoRoot 'artifacts\audio-probe'
    New-Item -ItemType Directory -Force -Path $probeDirectory | Out-Null
    $applicationPath = Join-Path $repoRoot "src\Sidecue.Desktop\bin\$Configuration\net10.0-windows10.0.26100.0\Sidecue.Desktop.exe"
    $probeProcess = Start-Process -FilePath $applicationPath -ArgumentList @('--probe-audio', ('"' + $probeDirectory + '"')) -PassThru -WindowStyle Hidden
    if (-not $probeProcess.WaitForExit(30000)) {
        $probeProcess.Kill()
        throw 'The local audio probe exceeded its 30-second limit.'
    }
    $probeProcess.WaitForExit()
    if ($probeProcess.ExitCode -ne 0) { throw "Audio probe failed with exit code $($probeProcess.ExitCode). Inspect artifacts/audio-probe/audio-probe.json." }
    Get-Content -LiteralPath (Join-Path $probeDirectory 'audio-probe.json')
} finally {
    Pop-Location
}
