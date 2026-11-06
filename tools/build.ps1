#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$UpdateLockFile
)

. (Join-Path $PSScriptRoot 'common.ps1')
Push-Location $repoRoot
try {
    Assert-SidecueSdk
    $restoreArguments = @('restore', 'Sidecue.slnx', '--configfile', 'NuGet.Config')
    if ($UpdateLockFile) { $restoreArguments += '--force-evaluate' }
    else { $restoreArguments += '--locked-mode' }
    Invoke-SidecueDotnet $restoreArguments
    Invoke-SidecueDotnet @('build', 'Sidecue.slnx', '--configuration', $Configuration, '--no-restore')
} finally {
    Pop-Location
}
