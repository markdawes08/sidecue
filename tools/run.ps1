#requires -Version 5.1
[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug
    Invoke-SidecueDotnet @('run', '--project', 'src/Sidecue.Desktop/Sidecue.Desktop.csproj',
        '--configuration', 'Debug', '--no-restore', '--no-build')
} finally {
    Pop-Location
}
