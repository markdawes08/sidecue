# Local development

Use Windows 11 x64 and the exact .NET SDK in [global.json](../global.json). No provider keys, meeting data or paid services are needed for the current build and tests.

If that SDK is not installed, this optional command downloads Microsoft's archive, checks its pinned SHA-512 checksum, and extracts it to ignored `artifacts/dotnet/`. It needs no machine-wide installation or PATH changes:

```powershell
powershell -NoProfile -File tools/setup-sdk.ps1
```

Run these commands from the repository root. The scripts prefer the local SDK above, otherwise use `dotnet` on PATH, and reject a different SDK version.

```powershell
# Locked restore, Release build, then all deterministic tests.
powershell -NoProfile -File tools/test.ps1

# Build alone, or test a build that already succeeded.
powershell -NoProfile -File tools/build.ps1
powershell -NoProfile -File tools/test.ps1 -NoBuild

# Build Debug and open the minimal desktop shell.
powershell -NoProfile -File tools/run.ps1
```

Both build and test accept `-Configuration Debug`; Release is the default. Test results are written as TRX files under ignored `artifacts/test-results/`. The scripts stop on restore, build or test errors. No provider calls occur in these tests.

## Project boundaries

| Project | Purpose |
| --- | --- |
| `src/Sidecue.Core` | Platform-independent domain layer |
| `src/Sidecue.Application` | Platform-independent application layer, referencing Core |
| `src/Sidecue.Windows` | Future Windows capture and windowing adapters |
| `src/Sidecue.Infrastructure` | Future storage and provider adapters |
| `src/Sidecue.Desktop` | WPF application and composition root |
| `tests/Sidecue.Architecture.Tests` | Project dependency direction and pure-layer restore checks |
| `tests/Sidecue.Desktop.Tests` | Compiled XAML, view-model binding and layout smoke check |

Only Desktop currently has product code: a small window explaining that meeting features are not yet available. The other four projects establish dependency boundaries; they contain no placeholder meeting logic. The immutable initial view model needs no observable-property framework. Add CommunityToolkit.Mvvm when actual changing UI state requires it.

## Dependency changes

Direct NuGet versions live in [Directory.Packages.props](../Directory.Packages.props). Review changes against official releases and licenses, then regenerate lockfiles deliberately:

```powershell
powershell -NoProfile -File tools/build.ps1 -UpdateLockFile
powershell -NoProfile -File tools/test.ps1 -NoBuild
```

Review all changed `packages.lock.json` files and include them in the user's commit. Ordinary builds and CI restore in locked mode. [NuGet.Config](../NuGet.Config) selects nuget.org rather than inheriting private feeds from the machine. SDK updates must update `global.json`, the reviewed checksum in `setup-sdk.ps1`, and the [dependency record](DEPENDENCIES.md) together.

## CI and test scope

[The Windows workflow](../.github/workflows/ci.yml) installs the pinned SDK, checks repository documents, restores locked packages, builds, runs the same tests and uploads TRX results. It uses `windows-2025` and logs the actual runner image version; that hosted image label is serviced over time. Action revisions are pinned to commits. The workflow needs only read access to source and has no production secrets or live AI calls.

Run checks relevant to the change and the selected task's required acceptance. These bootstrap tests do not prove audio capture, screen-share exclusion, encryption, provider quality or release packaging. Their tasks will add the appropriate tests and evidence when implemented. Packaging and interactive native test commands do not exist yet.

The README is intentionally left alone. Keep operational commands here and task context in [PROJECT_STATE.md](PROJECT_STATE.md).
