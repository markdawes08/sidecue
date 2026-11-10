#requires -Version 5.1
<#
Builds a Windows x64 SQLCipher Community candidate from pinned vendor sources.
Requires MSVC 14.51.36231, Windows SDK 10.0.28000.0 and native Windows Perl 5.44.0.
All downloads and outputs remain under ignored artifacts/sqlcipher-native.

Rebuilds are currently disabled: the pinned Perl 5.44.0 is affected by
CVE-2026-15534 and this recipe cannot verify the required upstream patches.
Re-enable only after selecting and verifying a patched Windows toolchain.

-ReuseLibcrypto is a diagnostic shortcut for an already reviewed build. It
requires the expected static library SHA-256 and does not prove a clean build.
Run the separate encrypted canary on the exact resulting DLL before use.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PerlExecutable,
    [string]$Vcvars64 = (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'),
    [string]$ReuseLibcrypto,
    [string]$ExpectedLibcryptoSha256
)

$ErrorActionPreference = 'Stop'

# A self-reported Perl version does not establish that both security fixes are
# present. Fail before downloading, extracting, compiling or replacing a DLL.
throw 'Native rebuild disabled: select and verify a patched Perl toolchain for CVE-2026-15534, then update this recipe.'

$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeRoot = Join-Path $repoRoot 'artifacts\sqlcipher-native'
$sqlArchive = Join-Path $nativeRoot 'sqlcipher-4.20.0.zip'
$sslArchive = Join-Path $nativeRoot 'openssl-official-3.5.9.tar.gz'
$sqlHash = 'a1417a5a672a2342de6ae44e08ccd68487fa3d0a43212331f4226a0d00b51b9c'
$sslHash = '603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a'
$sslOptions = 'VC-WIN64A no-shared no-asm no-tests no-module no-autoload-config no-dso'
$sqlOptions = '/DSQLITE_API=__declspec(dllexport) /DSQLITE_HAS_CODEC /DSQLITE_TEMP_STORE=3 /DSQLITE_EXTRA_INIT=sqlcipher_extra_init /DSQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown /DSQLITE_THREADSAFE=1 /DSQLCIPHER_CRYPTO_OPENSSL /DSQLITE_ENABLE_FTS5'

function Get-VerifiedArchive([string]$Path, [string]$Url, [string]$ExpectedHash) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Path
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedHash) { throw "Archive checksum mismatch: $Path" }
}

if (-not (Test-Path -LiteralPath $PerlExecutable -PathType Leaf)) { throw 'A native Windows Perl 5.44.0 executable is required.' }
if (-not (Test-Path -LiteralPath $Vcvars64 -PathType Leaf)) { throw 'The MSVC x64 vcvars64.bat path is required.' }
if ([bool]$ReuseLibcrypto -ne [bool]$ExpectedLibcryptoSha256) { throw 'Reuse requires both a static library path and its expected SHA-256.' }
if ($ReuseLibcrypto) {
    if ($ExpectedLibcryptoSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'ExpectedLibcryptoSha256 must contain 64 hexadecimal characters.' }
    if (-not (Test-Path -LiteralPath $ReuseLibcrypto -PathType Leaf)) { throw 'The requested reusable libcrypto does not exist.' }
    if ((Get-FileHash -LiteralPath $ReuseLibcrypto -Algorithm SHA256).Hash -ne $ExpectedLibcryptoSha256) { throw 'Reusable libcrypto checksum mismatch.' }
}

New-Item -ItemType Directory -Force -Path $nativeRoot | Out-Null
Get-VerifiedArchive $sqlArchive 'https://www.zetetic.net/downloads/sqlcipher/verify/4.20.0/sqlcipher-4.20.0.zip' $sqlHash
Get-VerifiedArchive $sslArchive 'https://www.openssl.org/source/openssl-3.5.9.tar.gz' $sslHash
$perlPath = (Resolve-Path -LiteralPath $PerlExecutable).Path
$vcvarsPath = (Resolve-Path -LiteralPath $Vcvars64).Path
$perlInfo = & $perlPath -e 'print $^O, q{|}, $^V;'
if ($LASTEXITCODE -ne 0 -or $perlInfo -ne 'MSWin32|v5.44.0') { throw 'Perl must be native Windows Perl 5.44.0.' }

$workRoot = Join-Path $nativeRoot ('build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot | Out-Null
Expand-Archive -LiteralPath $sqlArchive -DestinationPath $workRoot
& "$env:SystemRoot\System32\tar.exe" -xzf $sslArchive -C $workRoot
if ($LASTEXITCODE -ne 0) { throw 'OpenSSL archive extraction failed.' }
$sslSource = Join-Path $workRoot 'openssl-3.5.9'
$sqlSource = Join-Path $workRoot 'sqlcipher-4.20.0'
if ($ReuseLibcrypto) { Copy-Item -LiteralPath $ReuseLibcrypto -Destination (Join-Path $sslSource 'libcrypto.lib') }

$batch = @'
@echo off
call "%SIDECUE_VCVARS64%" 10.0.28000.0 -vcvars_ver=14.51.36231
if errorlevel 1 exit /b %errorlevel%
if not "%VCToolsVersion%"=="14.51.36231" exit /b 2
if not "%WindowsSDKVersion%"=="10.0.28000.0\" exit /b 2
cd /d "%SIDECUE_SSL_SOURCE%"
perl Configure VC-WIN64A no-shared no-asm no-tests no-module no-autoload-config no-dso
if errorlevel 1 exit /b %errorlevel%
nmake.exe /nologo build_generated
if errorlevel 1 exit /b %errorlevel%
if "%SIDECUE_REUSE_CRYPTO%"=="0" nmake.exe /nologo libcrypto.lib
if errorlevel 1 exit /b %errorlevel%
cd /d "%SIDECUE_SQL_SOURCE%"
nmake.exe /nologo /f Makefile.msc sqlite3.c
if errorlevel 1 exit /b %errorlevel%
cl.exe /nologo /O2 /MT /LD /DSQLITE_API=__declspec(dllexport) /DSQLITE_HAS_CODEC /DSQLITE_TEMP_STORE=3 /DSQLITE_EXTRA_INIT=sqlcipher_extra_init /DSQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown /DSQLITE_THREADSAFE=1 /DSQLCIPHER_CRYPTO_OPENSSL /DSQLITE_ENABLE_FTS5 /I"%SIDECUE_SSL_SOURCE%\include" sqlite3.c /link "%SIDECUE_SSL_SOURCE%\libcrypto.lib" crypt32.lib ws2_32.lib advapi32.lib user32.lib bcrypt.lib /OUT:sqlcipher.dll
if errorlevel 1 exit /b %errorlevel%
'@
$batchPath = Join-Path $workRoot 'build-native.cmd'
[System.IO.File]::WriteAllText($batchPath, $batch, [System.Text.Encoding]::ASCII)
$oldPath = $env:Path
$oldLcAll = $env:LC_ALL
$oldLang = $env:LANG
try {
    $env:Path = (Split-Path -Parent $perlPath) + ';' + $env:Path
    $env:LC_ALL = 'C'
    $env:LANG = 'C'
    $env:SIDECUE_VCVARS64 = $vcvarsPath
    $env:SIDECUE_SSL_SOURCE = $sslSource
    $env:SIDECUE_SQL_SOURCE = $sqlSource
    $env:SIDECUE_REUSE_CRYPTO = if ($ReuseLibcrypto) { '1' } else { '0' }
    & "$env:SystemRoot\System32\cmd.exe" /d /c $batchPath
    if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE. Work directory: $workRoot" }
} finally {
    $env:Path = $oldPath
    $env:LC_ALL = $oldLcAll
    $env:LANG = $oldLang
    Remove-Item Env:SIDECUE_VCVARS64, Env:SIDECUE_SSL_SOURCE, Env:SIDECUE_SQL_SOURCE, Env:SIDECUE_REUSE_CRYPTO -ErrorAction SilentlyContinue
}

$candidate = Join-Path $sqlSource 'sqlcipher.dll'
if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw 'The native build did not produce sqlcipher.dll.' }
$destination = Join-Path $nativeRoot 'sqlcipher.dll'
Copy-Item -LiteralPath $candidate -Destination $destination -Force
$buildInfo = [ordered]@{
    SqlCipherSourceSha256 = $sqlHash
    OpenSslSourceSha256 = $sslHash
    OpenSslOptions = $sslOptions
    SqlCipherOptions = $sqlOptions
    MsvcToolset = '14.51.36231'
    WindowsSdk = '10.0.28000.0'
    Perl = '5.44.0'
    ReusedLibcrypto = [bool]$ReuseLibcrypto
    LibcryptoSha256 = (Get-FileHash -LiteralPath (Join-Path $sslSource 'libcrypto.lib') -Algorithm SHA256).Hash.ToLowerInvariant()
    NativeSha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
}
$buildInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $nativeRoot 'build-info.json') -Encoding UTF8
$buildInfo | ConvertTo-Json
