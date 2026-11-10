# Current project context

Updated: November 9, 2026. Architecture baseline: 0.1. Task and gate status lives only in [backlog.json](../tasks/backlog.json).

Sidecue currently has a minimal WPF shell, an overlay prototype, separate Windows microphone and playback capture adapters, and a synthetic contract replay. Audio, providers and persistence are not connected to the shell. Overlay screen sharing and physical audio behavior are not fully verified.

SC-004B adds an infrastructure storage adapter: a random key protected with current-user DPAPI, user-restricted storage under `%LocalAppData%/SidecueData`, explicit recovery failures and an identity-checked SQLCipher loader. There is no transcript schema, packaged native DLL, installer or application wiring. Startup must own SQLitePCL initialization, and deployment must protect the native library's containing directories from untrusted modification. The local candidate remains in ignored build artifacts; system SQLite is not a fallback.

The [SC-004B evidence](evidence/SC-004B/2026-11-09.md) records a same-user process restart, concurrent live-WAL reopen, protected permissions, invalid keys, interrupted initialization, modified native DLL rejection and synthetic plaintext scans. The locked Release build and 153 existing tests passed; the focused probe passed after the final loader guard. Earlier [SC-004A evidence](evidence/SC-004A/2026-11-08.md) records the native candidate and canary. These results do not establish clean-VM, packaged-binary or portable-recovery behavior.

Read [the execution rules](IMPLEMENTATION_PLAN.md), the relevant [task card](tasks/SC-004.md), [data and recovery contracts](DATA_AND_RECOVERY.md) and [security data flow](SECURITY_AND_DATA_FLOW.md) before extending storage. [Dependencies](DEPENDENCIES.md) records package versions, licenses and the native build. Prior [audio](AUDIO_CAPTURE.md), [overlay](OVERLAY_PROTOTYPE.md), [contract](CONTRACT_RECORDS.md) and [replay](REPLAY.md) evidence remains available.

Run `powershell -NoProfile -File tools/show-task.ps1` for the next eligible leaf and blocked work. The next storage leaf is SC-004C, which requires a disposable clean Windows environment and retains exact-binary, durability, distribution and native-directory trust checks. Verify its prerequisites before claiming; do not treat this handoff as a gate pass. SC-002C remains blocked on mixed-scaling monitors and SC-003D on physical audio hardware and calibration. The owner's required actions remain on those backlog entries.

The owner handles staging, committing and pushing. Leave the root README alone until asked. Keep private data and generated artifacts ignored, preserve sanitized evidence and lockfiles, test the affected behavior proportionally, and stop after one executable leaf per prompt.
