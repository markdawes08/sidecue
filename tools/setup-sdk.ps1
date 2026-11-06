#requires -Version 5.1
# Installs only into the ignored workspace tooling directory, without admin changes.
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sdkVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
# Update the reviewed archive checksum together with global.json when servicing.
$reviewedVersion = '10.0.403'
$expectedHash = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48'
if ($sdkVersion -ne $reviewedVersion) { throw 'Review the SDK archive checksum for the new global.json version.' }
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) { throw 'This SDK setup command requires Windows x64.' }
$sdkDirectory = Join-Path $repoRoot 'artifacts\dotnet'
$sdkExecutable = Join-Path $sdkDirectory 'dotnet.exe'
if (Test-Path -LiteralPath $sdkExecutable) {
    Push-Location $repoRoot
    try {
        $installedVersion = & $sdkExecutable --version
        if ($LASTEXITCODE -eq 0 -and $installedVersion -eq $sdkVersion) {
            Write-Output "Local SDK $sdkVersion already present."
            exit 0
        }
    } finally {
        Pop-Location
    }
}
$archivePath = Join-Path $repoRoot "artifacts\dotnet-sdk-$sdkVersion-win-x64.zip"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $archivePath) | Out-Null
$archiveUrl = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip"
if (-not (Test-Path -LiteralPath $archivePath)) {
    Write-Output "Downloading .NET SDK $sdkVersion from Microsoft."
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing -Uri $archiveUrl -OutFile $archivePath
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash -ne $expectedHash) {
    throw "SDK checksum mismatch. Remove $archivePath and retry."
}
Expand-Archive -LiteralPath $archivePath -DestinationPath $sdkDirectory -Force
& $sdkExecutable --list-sdks
if ($LASTEXITCODE -ne 0) { throw 'The extracted SDK could not run.' }
Write-Output 'SDK ready in artifacts/dotnet; build and test scripts use it automatically.'
