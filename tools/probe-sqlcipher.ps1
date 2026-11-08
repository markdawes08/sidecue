#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$NativePath,
    [string]$ManifestPath,
    [switch]$NoBuild
)

. (Join-Path $PSScriptRoot 'common.ps1')
Push-Location $repoRoot
try {
    Assert-SidecueSdk
    if (-not $NativePath) { $NativePath = Join-Path $repoRoot 'artifacts\sqlcipher-native\sqlcipher.dll' }
    if (-not $ManifestPath) { $ManifestPath = Join-Path $repoRoot 'docs\evidence\SC-004A\candidate-manifest.json' }
    foreach ($probePath in @($NativePath, $ManifestPath)) {
        if (-not [System.IO.Path]::IsPathRooted($probePath) -or -not (Test-Path -LiteralPath $probePath -PathType Leaf)) {
            throw 'The native library and pinned manifest must be existing files with absolute paths.'
        }
    }
    $project = 'tests/Sidecue.CipherProbe/Sidecue.CipherProbe.csproj'
    if (-not $NoBuild) {
        Invoke-SidecueDotnet @('restore', $project, '--configfile', 'NuGet.Config', '--locked-mode')
        Invoke-SidecueDotnet @('build', $project, '--configuration', $Configuration, '--no-restore')
    }
    Invoke-SidecueDotnet @('run', '--project', $project, '--configuration', $Configuration,
        '--no-restore', '--no-build', '--', $NativePath, $ManifestPath)
} finally {
    Pop-Location
}
