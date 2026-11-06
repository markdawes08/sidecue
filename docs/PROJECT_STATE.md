# Current project context

Updated: November 6, 2026. Architecture baseline: 0.1.

## Current reality

Sidecue has a five-project .NET solution, a minimal WPF shell and an owned overlay prototype. The shell can show/hide the overlay; the overlay reports Windows capture-exclusion capability and retains an immediate hide path. It explicitly says screen sharing is unverified. There is no audio capture, persistence or provider connection. Core/Application provide the existing contracts; Windows now has the native window-affinity adapter. Infrastructure still has no adapters.

A separate test-only replay now runs a fictional meeting through controlled clock, capture, transcription, assistance and store fakes. It models revisions, duplicates, resets and stale-answer fences; these reference rules are not wired into the product.

SDK/package pins, lockfiles, recommended analyzers with warnings as errors, PowerShell setup/build/test/run/probe commands and Windows CI configuration are present. The isolated overlay result uses a small observable view model without a new package.

Task and gate status belongs only in [backlog.json](../tasks/backlog.json). Latest evidence: [SC-002A](evidence/SC-002A/2026-11-06.md); prototype operation and implementation: [overlay guide](OVERLAY_PROTOTYPE.md). SC-001's completed contract/replay evidence remains linked from its ledger entry. Read the [record mapping](CONTRACT_RECORDS.md), [service mapping](CONTRACT_SERVICES.md) and [replay guide](REPLAY.md) before extending those boundaries. Historical package provenance and source snapshots remain unchanged.

The plan has 32 feature-level groups containing 99 child tasks; SC-007 and SC-016 remain standalone. Original acceptance, dependencies, deferred scope and all five phase gates are preserved. Read [the execution rules](IMPLEMENTATION_PLAN.md) and [planning validation](evidence/planning/2026-11-06-task-split.md).

## Standing owner preferences

- Leave the root README alone until the user says otherwise. Use START_HERE.md, this handoff and task documents for context.
- The user handles all staging, committing and pushing. Include a suggested commit message when files change.
- Maintain .gitignore. Generated output, downloaded SDKs, private data and secrets stay out of Git; source, lockfiles, synthetic fixtures and sanitized evidence stay reviewable.
- Test the affected behavior and required task acceptance. Once relevant checks pass, expand only for a failure, dependency change or unresolved concern.
- One prompt defaults to one executable leaf, its checks and handoff. A parent ID selects its next eligible child. Stop there unless the user explicitly requests more blocks. Do not confuse a completed child with parent/gate acceptance.

## Verified commands and environment

See [DEVELOPMENT.md](DEVELOPMENT.md) for setup, commands and dependency updates; see [DEPENDENCIES.md](DEPENDENCIES.md) for official sources and version/license notes.

```powershell
# Optional, if the exact SDK is not on PATH:
powershell -NoProfile -File tools/setup-sdk.ps1

# Locked restore, Release build and deterministic tests:
powershell -NoProfile -File tools/test.ps1

# Synthetic contract replay and normalized observations:
powershell -NoProfile -File tools/replay.ps1

# Real local overlay HWND/API and lifecycle probe:
powershell -NoProfile -File tools/probe-overlay.ps1

# Open the minimal shell after a Debug build:
powershell -NoProfile -File tools/run.ps1
```

Local validation used Windows 11 Home 25H2, build 26200.9711, x64, PowerShell 5.1, SDK 10.0.403 and runtime 10.0.14. The system SDK remains 10.0.301; tooling uses the verified local SDK under ignored artifacts/dotnet. No machine-wide installation or persistent PATH change was made.

The locked Release build passed with zero warnings/errors. All 69 tests passed, including six new native-policy cases and six new WPF overlay lifecycle cases. Later changes only hardened the diagnostic output path and preview rendering; the affected native probe and failure path were rerun successfully.

On the actual desktop executable, DWM composition was enabled and both original/recreated overlay windows returned API acceptance with `0x11` readback. Hide/show, button hiding, recreation and owner cleanup passed at 150% DPI. The local synthetic preview was inspected. No receiving client or screen-capture path was observed, and no CAP row or phase gate has passed from these results.

The earlier [SC-000 run](evidence/SC-000/2026-11-06.md) verified a source-only export with fresh NuGet/CLI caches. That clean-export check was not repeated; this block exercised the actual Release executable through its explicit probe mode.

The Windows CI workflow targets windows-2025 and records the actual runner image. Hosted execution was not run or inspected in this block. The earlier clean source export was local, not a fresh OS installation.

## Next concrete action

Run `powershell -NoProfile -File tools/show-task.ps1` to select **SC-002B: overlay focus modes and keyboard controls**. Read its [task card](tasks/SC-002.md#sc-002b), parent acceptance and [overlay guide](OVERLAY_PROTOTYPE.md). Add non-activating display versus deliberate input modes, bounded hotkey handling and auxiliary-window exclusion policy. The current overlay activates normally when shown; local Escape is not a global emergency shortcut. Stop after this leaf. Physical focus/monitor and remote-observer verification remain SC-002C through SC-002F.

The task viewer is read-only. Pass `-Id` to inspect a specific leaf or select within a parent; pass `-Owner <agent-id>` to resume claimed work. Without an owner, automatic selection skips claimed tasks. Check external prerequisites and claim the selected leaf in backlog.json before work; do not assume the viewer has claimed it.

Exact SQLCipher selection, provider/model pins, supported hardware/share scenarios, signed packaging and measured latency remain unresolved. No phase gate is closed by the bootstrap. Keep the personal Windows workflow ahead of hosted/team features and preserve the existing data boundaries.
