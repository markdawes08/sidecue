# SC-009C Deepgram smoke preflight

## Identity and environment

| Field | Value |
| --- | --- |
| Evidence ID | SC-009C-2026-11-27-local |
| Task and coverage | SC-009C local preparation and opt-in guard. Real-provider checks remain pending. |
| Date and time | 2026-11-27T01:41:38-05:00 |
| Tester | Codex with independent harness review |
| Result | Local preflight passed. Live verification blocked. |
| Revision | Uncommitted changes based on `56ae96766570ce0c1860f86b7bfde987a7115c6e`, with the probe project and lockfile, PowerShell launcher, solution entry and documentation |
| Probe DLL SHA-256 | `b849550074278c88319b5523f70d22bc4961fb5ea74afb1adf8763ddda9382df` |
| Contract | Architecture baseline 0.1 and Core record schema 1 |

Windows 11 Home x64 25H2 build 26200.9974, Windows PowerShell 5.1.26100.9818 and .NET SDK 10.0.403. A locally installed English desktop voice generated both synthetic speech buffers in memory. The preflight used configuration `sc-009c-smoke-v1`, language `en-US`, 16 kHz mono signed little-endian PCM16 and a configured endpointing interval of 300 ms. Each fixture had 1.5 seconds of trailing silence. The scoped test credential and pinned model/version were absent from the current environment. No physical endpoints, provider account, transcript content or meeting client participated.

## Procedure

From the repository root in PowerShell:

```powershell
powershell -NoProfile -File tools/build.ps1
powershell -NoProfile -File tools/probe-deepgram.ps1
powershell -NoProfile -File tools/probe-deepgram.ps1 -NoBuild
& .\artifacts\dotnet\dotnet.exe test tests/Sidecue.Architecture.Tests/Sidecue.Architecture.Tests.csproj --configuration Release --no-restore --no-build
powershell -NoProfile -File tools/check-repository.ps1

[Environment]::SetEnvironmentVariable('SIDECUE_DEEPGRAM_TEST_KEY', $null, 'Process')
powershell -NoProfile -File tools/probe-deepgram.ps1 -Live -NoBuild
& .\artifacts\dotnet\dotnet.exe tests/Sidecue.DeepgramProbe/bin/Release/net10.0-windows10.0.26100.0/Sidecue.DeepgramProbe.dll --live
```

The build, both local preflight commands, all seven architecture tests and the repository checker exited 0. The build used locked restore and reported zero warnings and errors. The missing-key launcher and direct console guard exited 1 and 2 respectively. Both stopped before opening a connection. Clearing the variable for each guard happened in its disposable shell process. The focused checks cover the new probe, launcher and solution boundary. [Earlier adapter evidence](../SC-009B/2026-11-27.md)

## Results

| Case | Expected | Observed | Result |
| --- | --- | --- | --- |
| Microphone fixture | One bounded synthetic input with contiguous typed timing | 41 frames, 64,400 samples, 0 to 4,025,000 µs | pass |
| Playback fixture | Distinct bounded synthetic input with its own source and offset | 46 frames, 72,800 samples, 400,000 to 4,950,000 µs | pass |
| Explicit live guard | Launcher and direct executable reject live mode without the scoped key | Both returned an error naming the required setting. Their output contained no key, PCM or transcript. | pass |
| Configured Deepgram smoke | Two real connections with separate normalized source identities, finals and clean drain | Pending scoped test credential, pinned model/version and explicit `-Live` invocation. | not-run |

## Limits and next action

The probe is a console utility. Ordinary solution build and test do not launch it. Its launcher generates fixed synthetic speech and keeps the raw PCM in memory. Live mode uses the real adapter, zero reconnects and a 35-second overall deadline. It prints the requested model/version, bounded counts and safe status categories. The normalized adapter does not expose provider-reported model metadata. Interim events and utterance boundaries are counted when observed. Deepgram documents separate final results, trailing metadata and peer close for its stream drain. [Deepgram CloseStream documentation](https://developers.deepgram.com/docs/close-stream)

Resume SC-009C when the scoped test credential and intended model/version are available and the live run is explicitly enabled. Use the [development guide](../../DEVELOPMENT.md) for process-local credential setup. Record the real source-specific outcomes and assess the parent after the run. The backlog keeps SC-009C blocked and SC-009 open.

## Artifacts

| File | SHA-256 |
| --- | --- |
| `tools/probe-deepgram.ps1` | `a8767c94039f7fc88dc09139b74431370bb52cc8761be4cde11e27f16753a5ba` |
| `tests/Sidecue.DeepgramProbe/Program.cs` | `80d6059770ad89987c25d08ddfaa45a3220c0c18b64b53bfb9849b26b9a813c8` |

Build outputs remain ignored. The Markdown record contains counts and hashes only. No probe source changed after the preflight.
