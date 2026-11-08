# SC-003D physical audio preflight

## Identity and environment

| Field | Value |
| --- | --- |
| Evidence ID / task | SC-003D-2026-11-07-preflight / SC-003D |
| Result | partial; local baseline found a normalized-output shortfall; headset/device-transition and physical timing checks remain blocked or not run |
| Parent coverage | SC-003 validation 2 is not complete; no AUD matrix row or phase gate passes from this preflight |
| Time and tester | November 7, 2026, approximately 19:36–19:41 UTC-05:00; Codex coordinator and independent review |
| Revision | Uncommitted diagnostic changes based on `0ed4c93dee51ae089928527ccba467b8a5f42b78`; final changed build inputs are listed in [source-sha256.txt](source-sha256.txt) |
| Final desktop DLL SHA-256 | `05212ca224a38b7d2014529b1219e17e2c7f19a29ce9e71c5ad204cd1491080a` |
| Architecture / contract | Baseline 0.1 / schema 1; no dependency changes |

Windows 11 Home 25H2 build 26200.9711 x64, interactive session, PowerShell 5.1, workspace .NET SDK 10.0.403 and .NET/WindowsDesktop runtime 10.0.14. The available endpoints were an internal microphone and two playback endpoints. No present USB audio headset or second microphone endpoint was identified. The owner said a USB headset could not be connected now. No controlled physical timing reference was available. The device names and identifiers are omitted from evidence. No meeting client, provider, or receiving observer was used in the test.

## Procedure

The metadata-only diagnostic opens two independent recovery owners, renders synthetic silence, counts the raw samples passed to normalization and normalized frames received, and drains status streams. It does not write audio samples or change device defaults. It may transiently capture the local microphone and system playback mix in memory, including other applications; no sample values or meeting content were retained. Raw counts begin when a frame is transferred from the raw capture run to normalization; they do not count raw frames lost before that transfer.

From the repository root in Windows PowerShell:

```powershell
powershell -NoProfile -File tools/build.ps1
powershell -NoProfile -File tools/probe-audio-recovery.ps1 -NoBuild -Seconds 10
. ./tools/common.ps1
Invoke-SidecueDotnet @('test','tests/Sidecue.Desktop.Tests/Sidecue.Desktop.Tests.csproj','--configuration','Release','--no-restore','--no-build')
```

The first recovery baseline was run before adding the raw-consumption counter. After that instrumentation was added, the locked build and the 10-second diagnostic were repeated. Both builds exited 0 with zero warnings/errors; both diagnostic processes exited 0. Process success only means the diagnostic executed, not that a physical transition or AUD case passed. The affected Desktop suite passed 11 tests after the final build. A short `powershell -NoProfile -File tools/probe-audio.ps1 -NoBuild` native-clock probe also exited 0 between the two recovery runs; the native capture code was unchanged afterward.

No endpoint changes were performed during these runs. An attempt to open Windows Settings for an automated playback switch did not yield a targetable window, so no default device was changed. The USB unplug/reconnect, microphone default change and physical-reference checks were not performed. No user action is requested until the owner is ready to resume the backlog item.

## Results

| Check | Observed | Result |
| --- | --- | --- |
| Source and timeline | Both streams kept distinct source identities, the session timeline, ordered per-epoch frame sequences and mono 16 kHz PCM16 output; both ended Stopped | pass for this stationary baseline only |
| Loopback input/output accounting | 442,323 raw samples at 44.1 kHz were passed to normalization (10.030 s); 149,920 normalized samples at 16 kHz were delivered (9.370 s), a 660 ms duration shortfall, about 6.58% of consumed input | fail for the baseline continuity assessment; cause needs correction |
| Loopback gaps | 47 `CaptureDiscontinuity:Unknown` events reported an aggregate 681,261 microseconds, plus seven zero-duration `CaptureFailure:Unknown` events; no queue-overflow status and no terminal failure were observed | observed; reported intervals may overlap and do not establish exact lost-audio timing |
| Microphone accounting | 458,400 raw samples at 48 kHz (9.550 s) were passed to normalization; 152,160 output samples at 16 kHz (9.510 s) were delivered | observed, not a microphone quality or alignment pass |
| Native QPC consistency | 179/179 microphone starts and 0/203 loopback starts matched the QPC-derived start; all session intervals remained estimated | observed; not an independent physical timing measurement |
| Headset meeting and USB removal/reconnect | No USB headset was available | blocked |
| Default playback/microphone transition and calibrated timing | No transition was performed; second microphone and calibrated timing reference were not available | not-run / blocked |

The [final baseline aggregate](baseline-raw-comparison.json), SHA-256 `560fb9deab9ac63673a752a71342364527f5998997e17230366bda5f082b4afa`, contains counts and relative metadata only. The [earlier baseline](baseline-initial.json), SHA-256 `5a65c1b692ee967e3e5d5e193454d0915829aa72111aa7982385064366dae5a5`, recorded 49 unknown loopback discontinuities before raw counts were added. The [native-clock aggregate](native-clock-probe.json), SHA-256 `113fb111d7468afc105ca0fb2eb9c022d36b49822181ee8d9ab23732b9e247e3`, contains no audio. The earlier baseline and native probe preceded the final diagnostic-only instrumentation; their original build hashes were not retained.

The 660 ms is a difference between sample-derived durations in one silent-signal run. It does not identify which packets or words were lost, prove an exact physical gap, or establish the cause of the native QPC offset. Estimated-overlap packet discards and discarded pending resampler output are plausible contributors. The normalizer's pending-output discard path can emit a zero-length unknown failure gap; the aggregate does not quantify each branch. Do not correct the timing by guessing an offset or filling in fabricated audio.

## Completion assessment and follow-up

SC-003D remains blocked. The substantial baseline shortfall is recorded as corrective child SC-003E, which must run before the affected verification is repeated. SC-003D retains the USB headset meeting and reconnect, playback and microphone default changes, physical timing reference, and gap-impact verification. SC-002C remains separately blocked on mixed-DPI monitors. Parent SC-003 and its gate remain open.

The recovery diagnostic and sanitized results are reviewable; generated builds and working artifacts remain ignored. Existing `.gitignore` rules were sufficient. No raw audio, staging, commit or push was introduced. The root README was not consulted or changed.

Final repository checks exited 0: `powershell -NoProfile -File tools/check-repository.ps1`, `powershell -NoProfile -File tools/show-task.ps1`, `git diff --check`, `git diff --quiet -- README.md`, and verification of the listed source hashes. The checker validated 27 JSON files, 135 tasks, five gates, 275 local links and both original source hashes. The task viewer selects corrective leaf SC-003E and reports blocked SC-003D and SC-002C.
