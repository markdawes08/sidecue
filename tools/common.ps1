# Shared by the build, test and run entry points. No machine-wide settings change.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $repoRoot 'artifacts\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnetExecutable = $localDotnet
} else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) { throw 'Install the SDK from global.json or run tools/setup-sdk.ps1.' }
    $dotnetExecutable = $dotnetCommand.Source
}
$env:DOTNET_ROOT = Split-Path -Parent $dotnetExecutable
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-SidecueDotnet {
    param([string[]]$Arguments)
    & $dotnetExecutable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function Assert-SidecueSdk {
    $expected = (Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $actual = & $dotnetExecutable --version
    if ($LASTEXITCODE -ne 0 -or $actual -ne $expected) {
        throw "SDK $expected is required. Install it or run powershell -NoProfile -File tools/setup-sdk.ps1."
    }
}
