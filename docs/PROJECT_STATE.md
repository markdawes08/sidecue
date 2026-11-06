# Current project context

Updated: November 6, 2026. Architecture baseline: 0.1.

## Current reality

Sidecue has a five-project .NET solution and a minimal WPF window. The window states that meeting features are not available yet. It performs no audio capture, persistence or provider calls. Core contains validated records, owned audio frames, distinct session commands and immutable request/context/store envelopes. Application now defines the capture, transcription, clock, store, context and assistance interfaces. Windows and Infrastructure still have no adapters.

A separate test-only replay now runs a fictional meeting through controlled clock, capture, transcription, assistance and store fakes. It models revisions, duplicates, resets and stale-answer fences; these reference rules are not wired into the product.

SDK/package pins, lockfiles, recommended analyzers with warnings as errors, PowerShell setup/build/test/run commands and Windows CI configuration are present. The immutable shell view model needs no MVVM toolkit package yet; add the selected toolkit when mutable UI behavior needs it.

Task and gate status belongs only in [backlog.json](../tasks/backlog.json). The latest evidence is [SC-001C and parent completion](evidence/SC-001C/2026-11-06.md). Read the [record mapping](CONTRACT_RECORDS.md), [service mapping](CONTRACT_SERVICES.md) and [replay guide](REPLAY.md) before extending those boundaries. Schema 1 supports transcript-only context and the commit/read store subset. End/summary jobs, document retrieval, embeddings and screenshots remain later extensions. Historical package provenance and source snapshots remain unchanged.

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

# Open the minimal shell after a Debug build:
powershell -NoProfile -File tools/run.ps1
```

Local validation used Windows 11 Home 25H2, build 26200.9711, x64, PowerShell 5.1, SDK 10.0.403 and runtime 10.0.14. The system SDK remains 10.0.301; tooling uses the verified local SDK under ignored artifacts/dotnet. No machine-wide installation or persistent PATH change was made.

The latest locked Release build passed with zero warnings/errors. All 57 tests passed: seven replay, 15 Application, 27 Core, seven architecture and one WPF case. The replay entry point also passed. Two fresh fixture runs produced identical normalized observations matching explicit expectations: five fake commits, three current finals, six disposed buffers and both obsolete answers rejected. These results prove test-only reference behavior, not native queues, durable transactions or production cancellation/concurrency.

The earlier [SC-000 run](evidence/SC-000/2026-11-06.md) also verified a source-only export with fresh NuGet/CLI caches and opened/closed the actual Release shell. Those checks were not repeated for this test-only replay change.

The Windows CI workflow targets windows-2025 and records the actual runner image. Hosted execution was not run or inspected in this block. The earlier clean source export was local, not a fresh OS installation.

## Next concrete action

Run `powershell -NoProfile -File tools/show-task.ps1` to select **SC-002A: overlay with honest exclusion capability reporting**. Read its [task card](tasks/SC-002.md#sc-002a), parent acceptance and current official window-affinity documentation. Build the minimal owned overlay, injectable native adapter and immediate hide path; verify handle lifecycle and API outcomes locally. A successful API result does not prove remote capture exclusion. Stop after this leaf; receiving-client verification belongs to later SC-002 children.

The task viewer is read-only. Pass `-Id` to inspect a specific leaf or select within a parent; pass `-Owner <agent-id>` to resume claimed work. Without an owner, automatic selection skips claimed tasks. Check external prerequisites and claim the selected leaf in backlog.json before work; do not assume the viewer has claimed it.

Exact SQLCipher selection, provider/model pins, supported hardware/share scenarios, signed packaging and measured latency remain unresolved. No phase gate is closed by the bootstrap. Keep the personal Windows workflow ahead of hosted/team features and preserve the existing data boundaries.
