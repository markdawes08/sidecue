# Dependency record

Reviewed November 9, 2026 for SC-004B. The exact direct NuGet pins are in [Directory.Packages.props](../Directory.Packages.props); project lockfiles record the resolved transitive graph and content hashes. No cloud provider, UI styling or telemetry package has been introduced.

| Component | Selected version | Use and license |
| --- | --- | --- |
| .NET SDK | 10.0.403 | Build toolchain; official distribution includes its license and third-party notices |
| .NET / WindowsDesktop runtime | 10.0.14 in the selected SDK | Runtime used for validation; distribution notices apply |
| Windows SDK .NET reference pack | 10.0.26100.57, selected by SDK | Windows compile-time contracts; Microsoft distribution terms |
| NAudio.Wasapi | 3.3.0 | Shared-mode Windows microphone and render-loopback capture; MIT |
| Microsoft.Data.Sqlite.Core | 10.0.14 | Managed SQLite driver for the encrypted storage adapter and canary; MIT |
| SQLitePCLRaw.provider.dynamic_cdecl | 3.0.5 | Explicit native provider for encrypted storage; Apache-2.0 |
| System.Security.Cryptography.ProtectedData | 10.0.14 | Windows current-user DPAPI key protection; MIT |
| Microsoft.NET.Test.Sdk | 18.11.0 | Test tooling only; MIT |
| xunit.v3.mtp-off | 4.0.2 | xUnit tests with VSTest; Apache-2.0 |
| xunit.runner.visualstudio | 4.0.1 | VSTest adapter; Apache-2.0 |

Core and Application target `net10.0` and currently have no NuGet packages. Windows, Infrastructure and Desktop target `net10.0-windows10.0.26100.0`; Windows projects compile for x64. The declared minimum API platform is Windows build 22000, not a tested-support claim for that release. Supported Windows versions still require the project's compatibility evidence.

`global.json` disables SDK roll-forward. The runtime patch comes from the exact SDK's targeting configuration (`TargetLatestRuntimePatch`), with runtime roll-forward limited to servicing patches. This bootstrap is framework-dependent, not an installer or release package. Self-contained deployment is scheduled separately.

The installed system SDK 10.0.301/runtime 10.0.9 was behind current servicing. Microsoft identifies SDK 10.0.403/runtime 10.0.14, dated October 25, 2026, in its [official release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json). The [support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) requires current patches. The optional local setup verifies the official archive's SHA-512 before extraction; the hash is checked into `tools/setup-sdk.ps1`.

Package sources and licenses: [test SDK](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.11.0), [xUnit package](https://www.nuget.org/packages/xunit.v3.mtp-off/4.0.2), [VSTest adapter](https://www.nuget.org/packages/xunit.runner.visualstudio/4.0.1). The [xUnit runner documentation](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform) describes the VSTest-only package. This avoids introducing a second test platform for the initial suite.

The [ProtectedData package](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/10.0.14) provides Windows DPAPI access. Its `CurrentUser` scope binds protected keys to the user's Windows profile; it is not portable key recovery or database encryption by itself.

SC-003A pins [NAudio.Wasapi 3.3.0](https://www.nuget.org/packages/NAudio.Wasapi/3.3.0), which resolves NAudio.Core 3.3.0 and System.Numerics.Tensors 9.0.0 transitively in the lockfiles. NAudio is [MIT licensed](https://github.com/naudio/NAudio/blob/v3.3.0/LICENSE); the .NET library is [MIT licensed](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT). Its [recorder documentation](https://github.com/naudio/NAudio/blob/v3.3.0/Docs/WasapiRecorder.md) describes the callback-scoped audio span and native timing values. The adapter copies the callback data into its own bounded leases rather than retaining that span or writing audio files.

CommunityToolkit.Mvvm remains the selected UI framework when mutable state warrants it. Cloud provider packages and model pins must be reviewed in their owning tasks, not inferred from this record.

## Encrypted SQLite candidate

SC-004A evaluates a **Sidecue-built SQLCipher Community 4.20.0** x64 DLL against Microsoft.Data.Sqlite.Core 10.0.14 and SQLitePCLRaw's dynamic provider 3.0.5. The native build uses [Zetetic's signed source archive](https://www.zetetic.net/sqlcipher/verify/) (SHA-256 `a1417a5a672a2342de6ae44e08ccd68487fa3d0a43212331f4226a0d00b51b9c`) and statically linked [OpenSSL 3.5.9 LTS](https://www.openssl-library.org/source/) source (SHA-256 `603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a`). This is a project-built Community binary, not a Zetetic commercial distribution. Zetetic permits redistribution under its [BSD-style Community license](https://www.zetetic.net/sqlcipher/community/), including its copyright, conditions and disclaimer in user-accessible distribution materials. OpenSSL 3.x is Apache-2.0; its applicable notices must also accompany distribution. Neither component establishes FIPS certification for this build.

Zetetic identifies SQLite 3.54.1 as the [4.20.0 baseline](https://github.com/sqlcipher/sqlcipher/releases/tag/v4.20.0). SQLite documents the [WAL-reset corruption fix](https://sqlite.org/wal.html#the_wal_reset_bug) in 3.51.3 and later. The canary checks the actual loaded `sqlite_version()` and `sqlite_source_id()`, SQLCipher version, crypto provider, encryption status, FTS5 and `SQLITE_TEMP_STORE=3`. A reported version is interpreted together with the pinned source and actual binary hash; a NuGet package name or a connection-string password does not prove encryption. The older unofficial `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 is [deprecated and unmaintained](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlcipher/2.1.11) and was not selected.

The infrastructure adapter pins the reviewed DLL's SHA-256 and validates its loaded path and runtime identity before installing the provider. Startup must own SQLitePCL provider initialization, and deployment must supply the DLL from a directory protected from untrusted modification. The native DLL is generated under ignored `artifacts/`; it is not yet packaged. Each distinct binary must be reviewed and validated before its identity is accepted. A pinned source recipe does not prove byte-for-byte reproducibility across machines. SC-004C retains clean-Windows and reviewed-distribution verification, and the eventual release must test the exact binary it ships or match it to the tested artifact. Never substitute system SQLite if the reviewed SQLCipher build is absent. The adapter is not a transcript schema or release package.

## CI inputs

The [workflow](../.github/workflows/ci.yml) pins these official, MIT-licensed Actions:

| Action | Release | Commit |
| --- | --- | --- |
| [checkout](https://github.com/actions/checkout/releases/tag/v7.0.1) | 7.0.1 | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| [setup-dotnet](https://github.com/actions/setup-dotnet/releases/tag/v6.1.0) | 6.1.0 | `0f4a354dd5547a28c70b7853dbd95d5b67886e73` |
| [upload-artifact](https://github.com/actions/upload-artifact/releases/tag/v7.0.2) | 7.0.2 | `cf430e030ddbb5b0abf93d22962f4752f3646cd9` |

Runner: `windows-2025`. The [official image inventory](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md) reported image 20261101.318.1 at review; CI records its actual `ImageOS` and `ImageVersion` each run. A local Windows run does not constitute execution on that hosted image.
