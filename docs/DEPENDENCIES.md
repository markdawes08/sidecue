# Dependency record

Reviewed November 6, 2026 for SC-000. The exact direct NuGet pins are in [Directory.Packages.props](../Directory.Packages.props); project lockfiles record the resolved transitive graph and content hashes. No provider, database, capture, UI styling or telemetry package has been introduced.

| Component | Selected version | Use and license |
| --- | --- | --- |
| .NET SDK | 10.0.403 | Build toolchain; official distribution includes its license and third-party notices |
| .NET / WindowsDesktop runtime | 10.0.14 in the selected SDK | Runtime used for validation; distribution notices apply |
| Windows SDK .NET reference pack | 10.0.26100.57, selected by SDK | Windows compile-time contracts; Microsoft distribution terms |
| Microsoft.NET.Test.Sdk | 18.11.0 | Test tooling only; MIT |
| xunit.v3.mtp-off | 4.0.2 | xUnit tests with VSTest; Apache-2.0 |
| xunit.runner.visualstudio | 4.0.1 | VSTest adapter; Apache-2.0 |

Core and Application target `net10.0` and currently have no NuGet packages. Windows, Infrastructure and Desktop target `net10.0-windows10.0.26100.0`; Windows projects compile for x64. The declared minimum API platform is Windows build 22000, not a tested-support claim for that release. Supported Windows versions still require the project's compatibility evidence.

`global.json` disables SDK roll-forward. The runtime patch comes from the exact SDK's targeting configuration (`TargetLatestRuntimePatch`), with runtime roll-forward limited to servicing patches. This bootstrap is framework-dependent, not an installer or release package. Self-contained deployment is scheduled separately.

The installed system SDK 10.0.301/runtime 10.0.9 was behind current servicing. Microsoft identifies SDK 10.0.403/runtime 10.0.14, dated October 25, 2026, in its [official release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json). The [support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) requires current patches. The optional local setup verifies the official archive's SHA-512 before extraction; the hash is checked into `tools/setup-sdk.ps1`.

Package sources and licenses: [test SDK](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.11.0), [xUnit package](https://www.nuget.org/packages/xunit.v3.mtp-off/4.0.2), [VSTest adapter](https://www.nuget.org/packages/xunit.runner.visualstudio/4.0.1). The [xUnit runner documentation](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform) describes the VSTest-only package. This avoids introducing a second test platform for the initial suite.

CommunityToolkit.Mvvm remains the selected UI framework when mutable state is introduced; adding an unused package to the static shell provides no benefit. SQLCipher, NAudio, provider packages and model pins must be reviewed in their owning tasks, not inferred from this record.

## CI inputs

The [workflow](../.github/workflows/ci.yml) pins these official, MIT-licensed Actions:

| Action | Release | Commit |
| --- | --- | --- |
| [checkout](https://github.com/actions/checkout/releases/tag/v7.0.1) | 7.0.1 | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| [setup-dotnet](https://github.com/actions/setup-dotnet/releases/tag/v6.1.0) | 6.1.0 | `0f4a354dd5547a28c70b7853dbd95d5b67886e73` |
| [upload-artifact](https://github.com/actions/upload-artifact/releases/tag/v7.0.2) | 7.0.2 | `cf430e030ddbb5b0abf93d22962f4752f3646cd9` |

Runner: `windows-2025`. The [official image inventory](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md) reported image 20261101.318.1 at review; CI records its actual `ImageOS` and `ImageVersion` each run. A local Windows run does not constitute execution on that hosted image.
