# Current project context

Updated: November 6, 2026. Architecture baseline: 0.1.

## Current reality

Sidecue has a five-project .NET solution, a minimal WPF shell, an owned overlay prototype and separate Windows microphone/render-loopback capture adapters. The overlay has display and explicit local input modes, fixed global shortcuts and an excluded auxiliary details window. It explicitly says screen sharing is unverified. Audio capture is not connected to the shell, providers or persistence. Core/Application provide the contracts; Infrastructure still has no adapters.

A separate test-only replay now runs a fictional meeting through controlled clock, capture, transcription, assistance and store fakes. It models revisions, duplicates, resets and stale-answer fences; these reference rules are not wired into the product.

SDK/package pins, lockfiles, recommended analyzers with warnings as errors, PowerShell setup/build/test/run/probe commands and Windows CI configuration are present. The isolated overlay uses a small observable view model without a new package. Native system menus and WPF popup/tooltip surfaces are suppressed; IME/spellcheck popups are disabled for the prototype. Future surfaces must explicitly join the exclusion policy rather than assume inheritance from an owner.

Task and gate status belongs only in [backlog.json](../tasks/backlog.json). Latest evidence: [SC-003A](evidence/SC-003A/2026-11-06.md); operation and limits: [audio guide](AUDIO_CAPTURE.md). Overlay work remains documented in [SC-002C partial evidence](evidence/SC-002C/2026-11-06-preflight.md) and the [overlay guide](OVERLAY_PROTOTYPE.md). Read the [record mapping](CONTRACT_RECORDS.md), [service mapping](CONTRACT_SERVICES.md) and [replay guide](REPLAY.md) before extending those boundaries. Historical package provenance and source snapshots remain unchanged.

The plan has 32 feature-level groups containing 99 child tasks; SC-007 and SC-016 remain standalone. Original acceptance, dependencies, deferred scope and all five phase gates are preserved. Read [the execution rules](IMPLEMENTATION_PLAN.md) and [planning validation](evidence/planning/2026-11-06-task-split.md).

## Standing owner preferences

- Leave the root README alone until the user says otherwise. Use START_HERE.md, this handoff and task documents for context.
- The user handles all staging, committing and pushing. Include a suggested commit message when files change.
- Maintain .gitignore. Generated output, downloaded SDKs, private data and secrets stay out of Git; source, lockfiles, synthetic fixtures and sanitized evidence stay reviewable.
- Keep actionable follow-ups, blockers and evidence on the relevant open task in tasks/backlog.json; create scoped tasks only for work not already covered. Resolve or transfer remaining actions before closing a task.
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

# Brief local microphone/loopback probe, metadata only:
powershell -NoProfile -File tools/probe-audio.ps1

# Open the minimal shell after a Debug build:
powershell -NoProfile -File tools/run.ps1
```

Local validation used Windows 11 Home 25H2, build 26200.9711, x64, PowerShell 5.1, SDK 10.0.403 and runtime 10.0.14. The system SDK remains 10.0.301; tooling uses the verified local SDK under ignored artifacts/dotnet. No machine-wide installation or persistent PATH change was made.

The locked Release build passed with zero warnings/errors. The full suite passed 95 tests; after a focused audio stop-race and gap-reason correction, the 29 affected Windows tests passed again. The final native audio probe and existing overlay probe passed. SC-003A added NAudio.Wasapi 3.3.0 and regenerated the affected lockfiles.

The actual executable at 150% DPI accepted exclusion with `0x11` readback for original/recreated overlays and details. Native mode styles, input enablement, placement identity, hide/show and cleanup passed. All three shortcuts registered, a competing owner received error 1409, directed messages routed, and disposal/owner destruction released registrations. Windows declined the automated explicit foreground request; the UI displayed that result. Physical typing, key holding, another foreground application, mixed-DPI monitors and receiving streams were not verified. All three synthetic previews were inspected. No full WIN/CAP row or phase gate passed.

The final SC-003A two-second probe opened both Multimedia defaults, received 202 microphone and 202 loopback packets with separate identities and ordered sequences, and stopped both. It retained only metadata; synthetic silence drove loopback and no audio file was created. Both streams preserved raw device/QPC values, but all session intervals are explicitly uncertain callback-time estimates. SC-003B must address clock mapping, normalization and gap-aware timing; SC-003C/D cover device recovery and physical scenarios. No AUD matrix row or audio gate has passed.

SC-002B repository validation passed: 19 JSON files, 134 tasks, five gates, completion/coverage guards, 243 local links and both original source hashes. Whitespace checks passed and the README remained unchanged. Existing .gitignore rules cover generated output. Nothing was staged, committed or pushed.

The subsequent SC-002C partial run found one active 2560 x 1600 monitor at 150%; the owner cannot connect a second monitor now. On ordinary-integrity windows, injected shortcuts opened display, selected input mode and hid the overlay; a competing instance visibly reported conflicts, and details displayed its separate exclusion result. Typed text could not be read back, a shell Hide attempt with auxiliary windows open was inconclusive, and no separate foreground streaming-update test was run. The [partial record](evidence/SC-002C/2026-11-06-preflight.md) includes sanitized environment output and reproducible local helpers. No product code changed or full WIN/CAP row passed in that run.

Backlog maintenance now keeps specific return actions in an optional `follow_ups` array on the relevant open task. The task viewer always reports blocked leaves alongside the next selection; the repository checker rejects completed tasks with unresolved follow-ups. SC-002C has four recorded actions and remains blocked. The [maintenance check](evidence/backlog-maintenance/2026-11-06.md) covers viewer selection and completion guard behavior.

The earlier [SC-000 run](evidence/SC-000/2026-11-06.md) verified a source-only export with fresh NuGet/CLI caches. That clean-export check was not repeated; this block exercised the actual Release executable through its explicit probe mode.

The Windows CI workflow targets windows-2025 and records the actual runner image. Hosted execution was not run or inspected in this block. The earlier clean source export was local, not a fresh OS installation.

## Next concrete action

The next dependency-ready leaf is **SC-003B: normalize audio with bounded queues and gap-aware timing**. Read its [task card](tasks/SC-003.md#sc-003b), [audio guide](AUDIO_CAPTURE.md), capture contracts and backlog follow-up before claiming it. Preserve source/epoch identity, explicit loss and timing uncertainty. Stop after that leaf.

**SC-002C remains blocked** until two active monitors with different scaling are available. To resume, inspect it with `powershell -NoProfile -File tools/show-task.ps1 -Id SC-002C`, confirm the environment, clear its blocker and claim it. Finish verified text entry, a controlled streaming update against a separate foreground app, physical keyboard/hide/auxiliary behavior and mixed-DPI movement. The default task viewer reports blocked leaves while selecting available work. Receiving-client verification remains SC-002D through SC-002F.

The task viewer is read-only. Pass `-Id` to inspect a specific leaf or select within a parent; pass `-Owner <agent-id>` to resume claimed work. Without an owner, automatic selection skips claimed tasks. Check external prerequisites and claim the selected leaf in backlog.json before work; do not assume the viewer has claimed it.

Exact SQLCipher selection, provider/model pins, supported hardware/share scenarios, signed packaging and measured latency remain unresolved. No phase gate is closed by the bootstrap. Keep the personal Windows workflow ahead of hosted/team features and preserve the existing data boundaries.
