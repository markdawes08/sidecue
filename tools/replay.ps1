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
    Invoke-SidecueDotnet @('test', 'tests/Sidecue.Replay.Tests/Sidecue.Replay.Tests.csproj',
        '--configuration', $Configuration, '--no-restore', '--no-build',
        '--logger', 'trx', '--results-directory', (Join-Path $repoRoot 'artifacts\test-results'))
    Write-Host 'Replay observations: artifacts/replay/first-meeting-run1.json and first-meeting-run2.json'
} finally {
    Pop-Location
}
