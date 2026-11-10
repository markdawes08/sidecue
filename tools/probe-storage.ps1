#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$NativePath,
    [switch]$NoBuild
)

. (Join-Path $PSScriptRoot 'common.ps1')
Push-Location $repoRoot
try {
    Assert-SidecueSdk
    if (-not $NativePath) { $NativePath = Join-Path $repoRoot 'artifacts\sqlcipher-native\sqlcipher.dll' }
    if (-not [System.IO.Path]::IsPathRooted($NativePath) -or -not (Test-Path -LiteralPath $NativePath -PathType Leaf)) {
        throw 'The approved native library must be an existing file with an absolute path.'
    }
    $project = 'tests/Sidecue.StorageProbe/Sidecue.StorageProbe.csproj'
    if (-not $NoBuild) {
        Invoke-SidecueDotnet @('restore', $project, '--configfile', 'NuGet.Config', '--locked-mode')
        Invoke-SidecueDotnet @('build', $project, '--configuration', $Configuration, '--no-restore')
    }
    $probeDirectory = Join-Path $repoRoot ('artifacts\storage-probe\' + [guid]::NewGuid().ToString('N'))
    Invoke-SidecueDotnet @('run', '--project', $project, '--configuration', $Configuration,
        '--no-restore', '--no-build', '--', $NativePath, $probeDirectory)
} finally {
    Pop-Location
}
