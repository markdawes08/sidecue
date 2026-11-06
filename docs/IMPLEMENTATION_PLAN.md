# Sidecue implementation plan

This plan translates the architecture into small implementation tasks. Read [AGENTS.md](../AGENTS.md) first. The live task state, dependencies, ownership, blockers, and evidence references are held only in [tasks/backlog.json](../tasks/backlog.json). This document explains how to execute and assess that work; it is not a second status tracker.

## How to select and complete work

1. Read the current task, its dependencies, required phase gates, and relevant contracts before changing code. Inspect the repository rather than assuming this package represents implemented features.
2. Work on one coherent task at a time. An agent may delegate disjoint subparts, but one owner integrates and verifies the task. Coordinate shared contract, project-file, migration and backlog changes.
3. Promote a planned task to ready only when its dependencies are done and its required gates have passed. If hardware access blocks a gate, continue independent tasks whose own prerequisites permit it. Do not remove the gate or label a mock result as Windows proof.
4. When the code is implemented but a required physical/provider verification is unavailable, use implemented_unverified or blocked with an exact next validation action. Keep the phase gate pending or blocked.
5. A done task has its acceptance behavior implemented and its listed evidence recorded. Commit/worktree details, commands, outcomes, limitations and evidence references must be truthful. Do not set a gate to passed merely because its code compiles.
6. Recheck current official provider/native-library documentation when implementing version-sensitive details. Keep the accepted architecture unless new evidence calls for a documented decision update. Resolve routine implementation details autonomously; a task does not grant authority to publish, purchase, or send messages.

## Execution shape

Phase 0 validates native risks before the large interface. Core contracts, replay tests and selected independent infrastructure can advance while a physical check is blocked, but an integrated milestone cannot skip its gate. Production-owned paths below are the intended future layout, not evidence that code already exists. Evidence directories under docs/evidence are created by the task that produces actual results.

Keep each change reviewable. If a task becomes too large, split it in backlog.json while preserving dependency and phase-gate coverage. Retain the original task as a coordination parent linked to its children rather than silently discarding acceptance criteria. Keep its existing status vocabulary; it becomes done only after all child work and original acceptance evidence are complete. Do not invent a superseded status or remove a gate dependency. Reconcile the plan when task boundaries change.

## Phase gates

| Gate | Purpose | Required evidence |
| --- | --- | --- |
| G0 | Windows and packaging feasibility | Observer-side capture exclusion, real two-source audio/device transition, opt-in screenshot behavior, encrypted native database, clean-machine installation |
| G1 | Reliable transcription | Durable revisions/finals, explicit gaps, recovery, two-hour physical meeting loop |
| G2 | Useful live assistance | Request isolation, cancellation, selected model evaluation, real overlay demonstration |
| G3 | Grounded personal workflow | Import/index/retrieval provenance, editable notes, deletion, explicit follow-up drafts |
| G4 | Personal release readiness | Supported platform matrix, accessibility, signed updates, same-profile restore, overnight soak, reviewed build evidence |

The JSON gate records carry the live state. G1 through G4 also require the preceding gate to pass. Passing G4 prepares a release; it does not itself publish anything.

## Task index

| Task | Phase | Deliverable |
| --- | --- | --- |
| [SC-000](#sc-000) | 0 | Bootstrap the solution and repeatable checks |
| [SC-001](#sc-001) | 0 | Implement domain contracts and deterministic replay |
| [SC-002](#sc-002) | 0 | Prove overlay focus and capture exclusion |
| [SC-003](#sc-003) | 0 | Prove two-source capture and stream epochs |
| [SC-004](#sc-004) | 0 | Prove encrypted database and protected keys |
| [SC-005](#sc-005) | 0 | Prove selected-target screenshot capture |
| [SC-006](#sc-006) | 0 | Prove installation and data preservation |
| [SC-007](#sc-007) | 1 | Implement the meeting lifecycle |
| [SC-008](#sc-008) | 1 | Persist transcript revisions and finalized segments |
| [SC-009](#sc-009) | 1 | Add the Deepgram streaming adapter |
| [SC-010](#sc-010) | 1 | Connect capture to durable live transcription |
| [SC-011](#sc-011) | 1 | Build diagnostics and the minimal transcript dashboard |
| [SC-012](#sc-012) | 1 | Recover interrupted sessions and incomplete endings |
| [SC-013](#sc-013) | 1 | Validate a two-hour transcription session |
| [SC-014](#sc-014) | 2 | Build versioned profiles and bounded context snapshots |
| [SC-015](#sc-015) | 2 | Implement the streaming generation adapter |
| [SC-016](#sc-016) | 2 | Coordinate cancellation and request priority |
| [SC-017](#sc-017) | 2 | Integrate the overlay and global commands |
| [SC-018](#sc-018) | 2 | Deliver manual meeting actions and usage feedback |
| [SC-019](#sc-019) | 2 | Select model defaults and validate assistance quality |
| [SC-020](#sc-020) | 3 | Import text documents with stable provenance |
| [SC-021](#sc-021) | 3 | Create versioned embedding and keyword indexes |
| [SC-022](#sc-022) | 3 | Retrieve evidence and validate citations |
| [SC-023](#sc-023) | 3 | Attach an approved screenshot to one request |
| [SC-024](#sc-024) | 3 | Generate grounded meeting artifacts through durable jobs |
| [SC-025](#sc-025) | 3 | Add editable notes and searchable meeting history |
| [SC-026](#sc-026) | 3 | Implement retention and deletion across derived data |
| [SC-027](#sc-027) | 3 | Prepare editable follow-up drafts and complete grounded workflow |
| [SC-028](#sc-028) | 4 | Harden audio selection and speakerphone attribution |
| [SC-029](#sc-029) | 4 | Complete device power and sharing compatibility checks |
| [SC-030](#sc-030) | 4 | Finish onboarding accessibility and language behavior |
| [SC-031](#sc-031) | 4 | Harden signed updates and release installation |
| [SC-032](#sc-032) | 4 | Validate encrypted backup and restore boundaries |
| [SC-033](#sc-033) | 4 | Run soak performance and privacy acceptance |
| [SC-034](#sc-034) | 4 | Assemble personal release evidence and operating guide |

## Task cards

### SC-000

**Bootstrap the solution and repeatable checks**

Phase: 0. Dependencies: none. Required gates: none beyond task dependencies.

Scope:

- Create the five production projects with the architecture dependency direction and xUnit test projects.
- Pin an installed supported .NET 10 SDK patch and reviewed packages, centralize versions, enable nullable/analyzers, and add PowerShell build/test entry points.
- Add Windows CI with no production keys or live provider calls.

Acceptance:

- Clean Windows checkout restores, builds, and runs deterministic tests through documented commands.
- Core and Application do not depend on WPF, Windows APIs, or provider SDKs.
- Working app is a minimal shell; no feature is represented as already complete.

Verification and evidence:

- Capture exact build/test commands, SDK/package lock state, runner image and successful outputs.
- Verify a clean checkout can repeat the process without untracked local setup.

Expected owned areas: `Sidecue.sln or Sidecue.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `src/*/*.csproj`, `tests/*/*.csproj`, `tools/build.ps1`, `tools/test.ps1`, `.github/workflows/ci.yml`.

### SC-001

**Implement domain contracts and deterministic replay**

Phase: 0. Dependencies: SC-000. Required gates: none beyond task dependencies.

Scope:

- Implement the smallest typed records/interfaces needed for capture, transcript events, session commands, context snapshots, and generation events.
- Create deterministic clock, capture, transcriber, provider, and persistence fakes and a replay runner.

Acceptance:

- A synthetic meeting replays without hardware, network access, or provider keys.
- Events preserve session, source, stream epoch, sequence, monotonic time and UTC roles.
- Interface contract version and mapping rules are documented alongside implementation.

Verification and evidence:

- Replay duplicate, reordered, partial/final, stream-reset, and late-answer events.
- Assert deterministic output for a fixed fixture and explicit rejection of invalid event identities.

Expected owned areas: `src/Sidecue.Core/`, `src/Sidecue.Application/Abstractions/`, `tests/Sidecue.Replay.Tests/`, `tests/fixtures/`, `tools/replay.ps1`.

### SC-002

**Prove overlay focus and capture exclusion**

Phase: 0. Dependencies: SC-000. Required gates: none beyond task dependencies.

Scope:

- Create a small owned top-level WPF overlay and apply WDA_EXCLUDEFROMCAPTURE with result/error reporting.
- Prove non-activating display, interactive input, hide/show and hotkey conflict handling.
- Exercise auxiliary windows and monitor/DPI movement.

Acceptance:

- A real Windows observer recording verifies the chosen supported screen-share paths.
- Unsupported or failed exclusion remains visible as a limitation with immediate hide available.
- Streaming display does not steal meeting focus; keyboard entry remains usable.

Verification and evidence:

- Record OS, GPU/driver, client/browser versions, share mode and actual remote-observer result for Teams, Zoom and Meet baseline cases.
- Test two monitors, mixed DPI, popups/tooltips/dialogs and hotkey conflicts. API return success alone does not pass.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Windows/Windowing/`, `src/Sidecue.Desktop/Prototypes/Overlay/`, `tests/Sidecue.Windows.Tests/Windowing/`, `docs/evidence/SC-002/`.

### SC-003

**Prove two-source capture and stream epochs**

Phase: 0. Dependencies: SC-001. Required gates: none beyond task dependencies.

Scope:

- Capture mic and render endpoint loopback separately through WASAPI.
- Normalize on worker threads, preserve source/time/epoch metadata, and demonstrate bounded queues.
- Detect invalidated devices and reopen a source with a new epoch and explicit gap.

Acceptance:

- A headset meeting produces distinct microphone and remote streams with no intentional audio files.
- USB removal/default-output change creates a visible discontinuity instead of silently relabeling the same stream.
- The capture callback never waits for UI, network, inference or database work.

Verification and evidence:

- Use synthetic overload to verify bounded memory and counted/delimited gaps.
- Run physical microphone/loopback and device-change checks with metadata-only measurements.
- Replay a dropped middle interval and verify later provider timestamps map to original session time, with uncertain spans labelled.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Windows/Audio/`, `tests/Sidecue.Windows.Tests/Audio/`, `tests/Sidecue.Replay.Tests/Audio/`, `docs/evidence/SC-003/`.

### SC-004

**Prove encrypted database and protected keys**

Phase: 0. Dependencies: SC-000. Required gates: none beyond task dependencies.

Scope:

- Select and pin a maintained distributable SQLCipher native build with a compatible Microsoft.Data.Sqlite.Core integration.
- Protect a random key using current-user DPAPI, keep data in SidecueData outside the installer root, and prove WAL/FTS behavior.
- Record native versions, license/distribution basis, and the documented SQLite WAL fix or backport.

Acceptance:

- A wrong key fails and plaintext fixtures are absent from database/WAL/temporary artifacts.
- A restart under the same Windows user can read the data; expected failure without the protected key is explicit.
- FTS/search and migrations function with encryption enabled; no plaintext fallback exists.

Verification and evidence:

- Test wrong key, reopen, transaction interruption, FTS query, migration and native-library loading on a clean Windows environment.
- Inspect native SQLite/SQLCipher runtime versions, not just NuGet versions.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Infrastructure/Persistence/`, `src/Sidecue.Infrastructure/Security/`, `tests/Sidecue.Infrastructure.Tests/Encryption/`, `docs/evidence/SC-004/`.

### SC-005

**Prove selected-target screenshot capture**

Phase: 0. Dependencies: SC-000, SC-002. Required gates: none beyond task dependencies.

Scope:

- Use Windows.Graphics.Capture and the system target picker for one requested image.
- Show preview before explicit inclusion, dispose frames, and keep snapshots in memory.
- Exercise resize, target closure, multiple same-resolution monitors, and device loss handling.

Acceptance:

- Selection uses native target identity and never falls back to a different display without selection.
- Cancelling the picker/preview sends no image and leaves no screenshot file.
- Unsupported/HDR/resizing cases are reported or processed correctly under the supported matrix.

Verification and evidence:

- Perform real Windows picker/preview/cancel/resize/target-close checks and inspect temporary-file behavior.
- Use device-loss and cancellation fakes to verify resource disposal.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Windows/ScreenCapture/`, `src/Sidecue.Desktop/Prototypes/ScreenCapture/`, `tests/Sidecue.Windows.Tests/ScreenCapture/`, `docs/evidence/SC-005/`.

### SC-006

**Prove installation and data preservation**

Phase: 0. Dependencies: SC-000, SC-004. Required gates: none beyond task dependencies.

Scope:

- Package the minimal shell as a self-contained win-x64 Velopack build.
- Keep database, keys and backups outside RootAppDir and verify standard-user installation.
- Exercise an internal upgrade/uninstall and state the signing/trust configuration used.

Acceptance:

- A clean Windows machine without developer tools installs and starts the shell.
- Upgrade and ordinary uninstall preserve user data and its protected key.
- Unsigned development-package evidence is labelled as development-only; release signing is still a later requirement.

Verification and evidence:

- Record clean-machine install/start/upgrade/uninstall/reinstall evidence.
- Verify data path is outside installer-owned directories and native encryption libraries load after packaging.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `tools/package.ps1`, `packaging/`, `src/Sidecue.Desktop/Startup/`, `tests/Sidecue.Infrastructure.Tests/Paths/`, `docs/evidence/SC-006/`.

### SC-007

**Implement the meeting lifecycle**

Phase: 1. Dependencies: SC-001. Required gates: none beyond task dependencies.

Scope:

- Implement Idle/Starting/Listening/Paused/Stopping/Completed and Interrupted/RecoveryRequired outcomes.
- Keep capture state separate from generation state and serialize lifecycle commands.
- Define Pause listening, Hide, Clear context and End as distinct operations.

Acceptance:

- Repeated start/pause/end commands are safe and produce deterministic states.
- Pause cuts off capture/uploads and drops unsent audio while permitting approved-context generation.
- Clear context increments epoch and cancels generation without deleting saved history.

Verification and evidence:

- Replay command races, pause during start, stop during reconnect, clear during generation and repeated terminal commands.

Expected owned areas: `src/Sidecue.Application/Sessions/`, `src/Sidecue.Core/Sessions/`, `tests/Sidecue.Application.Tests/Sessions/`.

### SC-008

**Persist transcript revisions and finalized segments**

Phase: 1. Dependencies: SC-001, SC-004. Required gates: none beyond task dependencies.

Scope:

- Add ordered migrations and the session/stream/segment/gap schema.
- Implement a serialized writer with short transactions and idempotent final-segment writes.
- Keep partial display state separate from finalized evidence.

Acceptance:

- Duplicate provider final messages cannot create duplicate transcript content.
- Stream epochs and time ranges survive restart and corrected revisions follow an explicit rule.
- No partial text is silently promoted to final text.

Verification and evidence:

- Integration tests cover duplicated/reordered events, migration rollback, foreign keys and store reopen.
- Verify encrypted storage and WAL are still active for the production schema.

Expected owned areas: `src/Sidecue.Infrastructure/Persistence/`, `src/Sidecue.Application/Transcripts/`, `tests/Sidecue.Infrastructure.Tests/Transcripts/`, `tests/Sidecue.Replay.Tests/Transcripts/`.

### SC-009

**Add the Deepgram streaming adapter**

Phase: 1. Dependencies: SC-001. Required gates: none beyond task dependencies.

Scope:

- Implement separate microphone and remote WebSocket sessions behind IStreamingTranscriber.
- Map is_final, speech_final, source identity, diarization scope, errors and reconnect epochs to contracts.
- Load a configured model, format and endpointing setting without embedding keys in code.

Acceptance:

- Partial/final/utterance-end semantics remain distinct.
- Connection loss is visible and begins a new epoch without unbounded buffering or blind replay.
- Invalid credentials are actionable and do not trigger endless retries or provider switching.

Verification and evidence:

- Use recorded synthetic protocol events for finalization, close, timeout, malformed message and authentication failures.
- Run a separately enabled synthetic-audio smoke test only with configured credentials; save no secret payloads.
- Test sent-sample interval mapping across queue overflow and reconnect; provider offsets must not assume missing audio was transmitted.

Expected owned areas: `src/Sidecue.Infrastructure/Transcription/`, `tests/Sidecue.Infrastructure.Tests/Transcription/`, `tests/fixtures/providers/`.

### SC-010

**Connect capture to durable live transcription**

Phase: 1. Dependencies: SC-003, SC-007, SC-008, SC-009. Required gates: G0.

Scope:

- Wire two capture sources through bounded normalization/transcription channels and transcript persistence.
- Report gaps, queue pressure and persistence failures directly to the session controller.
- Stop capture in RecoveryRequired before the bounded unsaved-text buffer fills.

Acceptance:

- Two-source speech appears live and committed segments survive restart.
- Artificially slow consumers cannot cause unbounded memory growth.
- Disk-full/locked-store failures label unsaved text and pause acquisition rather than claiming a save.

Verification and evidence:

- Replay backpressure and failed writes with a deterministic clock.
- Exercise real headset capture with a network interruption and validate source/epoch labels.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Application/Capture/`, `src/Sidecue.Application/Transcripts/`, `tests/Sidecue.Application.Tests/Pipeline/`, `docs/evidence/SC-010/`.

### SC-011

**Build diagnostics and the minimal transcript dashboard**

Phase: 1. Dependencies: SC-007, SC-010. Required gates: none beyond task dependencies.

Scope:

- Add source selection, audio meters, provider readiness, session start/pause/end and a virtualized transcript view.
- Expose recording/capture state and unsaved/degraded states clearly.
- Provide copy/export of committed transcript text.

Acceptance:

- Users can verify each source before starting and tell capture state without inspecting logs.
- Long transcripts do not freeze scrolling or control input.
- An export distinguishes gaps and provisional/incomplete content.

Verification and evidence:

- UI Automation covers source validation, start/pause/end and export.
- Load a long replay and measure dispatcher response; test no-device and denied-permission states.

Expected owned areas: `src/Sidecue.Desktop/Views/Diagnostics/`, `src/Sidecue.Desktop/Views/Session/`, `src/Sidecue.Desktop/ViewModels/`, `tests/Sidecue.Ui.Tests/`.

### SC-012

**Recover interrupted sessions and incomplete endings**

Phase: 1. Dependencies: SC-007, SC-008, SC-010. Required gates: none beyond task dependencies.

Scope:

- Persist lifecycle transitions and recognize unclean shutdowns.
- Implement bounded stop draining with an unresolved tail marker on timeout.
- Allow recovery/explicit export of available committed data without inventing missing audio.

Acceptance:

- A crash returns committed transcript and an Interrupted state.
- Drain timeout marks the transcript incomplete and retained partials provisional.
- Retry/export paths remain available after a failed write without silently replacing user data.

Verification and evidence:

- Kill the test app during capture, commit and stop; reopen and compare committed evidence.
- Simulate provider tail never finalizing and storage unavailability during error reporting.

Expected owned areas: `src/Sidecue.Application/Recovery/`, `src/Sidecue.Infrastructure/Persistence/`, `tests/Sidecue.Infrastructure.Tests/Recovery/`, `docs/evidence/SC-012/`.

### SC-013

**Validate a two-hour transcription session**

Phase: 1. Dependencies: SC-011, SC-012. Required gates: G0.

Scope:

- Run the integrated meeting loop with synthetic/authorized test speech and real devices.
- Measure latency, memory, gaps, source attribution and recovery through interruptions.
- Resolve discovered pipeline defects before closing the phase.

Acceptance:

- Two-hour run shows bounded non-history memory and responsive pause/hide controls.
- Every injected outage has an explicit observed outcome and no silent transcript corruption.
- Evidence states exact OS/device/provider settings and measured p50/p95 values.

Verification and evidence:

- Exercise USB/default-device changes, network disconnect, overlapping speech, pause and end timeout.
- Inspect logs for secrets, prompts and transcript content.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `docs/evidence/SC-013/`, `tests/Sidecue.Replay.Tests/`, `tools/diagnostics.ps1`.

### SC-014

**Build versioned profiles and bounded context snapshots**

Phase: 2. Dependencies: SC-001, SC-008. Required gates: none beyond task dependencies.

Scope:

- Implement immutable prompt profile versions and live/detailed response settings.
- Assemble recent transcript, source-linked summary and evidence slots under the model token budget.
- Pin transcript revision, profile version and context epoch per request.

Acceptance:

- The saved transcript is unaffected by prompt trimming or clear context.
- Snapshots are immutable and reserve output capacity within configured limits.
- A profile change affects subsequent requests while earlier artifacts retain their original version.

Verification and evidence:

- Test long transcripts, token limits, empty context, profile switching, summary provenance and stale epochs.

Expected owned areas: `src/Sidecue.Application/Context/`, `src/Sidecue.Infrastructure/Profiles/`, `prompts/`, `tests/Sidecue.Application.Tests/Context/`.

### SC-015

**Implement the streaming generation adapter**

Phase: 2. Dependencies: SC-001, SC-014. Required gates: none beyond task dependencies.

Scope:

- Implement the initial OpenAI adapter for text and declared image capability.
- Normalize streaming text, completion, cancellation, failure and available usage events.
- Keep model IDs configurable and provider fallback restricted to explicitly enabled providers.

Acceptance:

- Fake streaming responses and configured live requests use the same application contract.
- Timeouts/authentication/limits return typed failures with bounded retries.
- No raw model reasoning, keys or prompt bodies enter diagnostic logs.

Verification and evidence:

- Contract tests cover partial output, mid-stream failure, cancellation, malformed events and usage absence.
- Use a separately enabled synthetic-prompt provider smoke test and record the selected model/configuration.

Expected owned areas: `src/Sidecue.Infrastructure/Generation/`, `tests/Sidecue.Infrastructure.Tests/Generation/`, `tests/fixtures/providers/`.

### SC-016

**Coordinate cancellation and request priority**

Phase: 2. Dependencies: SC-007, SC-014, SC-015. Required gates: none beyond task dependencies.

Scope:

- Implement a single foreground request slot with manual priority.
- Cancel/supersede previous requests and reject output with obsolete session, context epoch or request ID.
- Persist completed/cancelled/failed artifact status separately from rendered deltas.

Acceptance:

- A late response from a prior request cannot overwrite a newer response.
- Clear context and session end invalidate the intended work deterministically.
- Manual requests are not starved by background suggestions or summaries.

Verification and evidence:

- Race tests interleave start, clear, cancellation, completion and out-of-order deltas across two sessions.

Expected owned areas: `src/Sidecue.Application/Assistance/`, `tests/Sidecue.Application.Tests/Assistance/`.

### SC-017

**Integrate the overlay and global commands**

Phase: 2. Dependencies: SC-002, SC-011, SC-016. Required gates: G0.

Scope:

- Replace prototype wiring with a view-model-driven command bar and streamed answer panel.
- Implement customizable hotkeys, explicit interactive/non-activating modes and saved monitor placement.
- Connect clear/hide/pause/end to their distinct application commands.

Acceptance:

- Answers stream without focus theft and the user can enter a question deliberately.
- Hotkey conflicts are reported and disabled bindings work.
- Every auxiliary window follows tested capture-exclusion behavior and has a reliable hide path.

Verification and evidence:

- UI tests cover keyboard-only operation, rapid requests, hide/show and context clear.
- Repeat physical focus and baseline screen-share checks after integrating the actual controls.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Desktop/Views/Overlay/`, `src/Sidecue.Desktop/ViewModels/Overlay/`, `src/Sidecue.Windows/Hotkeys/`, `tests/Sidecue.Ui.Tests/Overlay/`, `docs/evidence/SC-017/`.

### SC-018

**Deliver manual meeting actions and usage feedback**

Phase: 2. Dependencies: SC-014, SC-016, SC-017. Required gates: none beyond task dependencies.

Scope:

- Add Ask, Suggest what to say, Follow-up questions and Recap with concise response defaults.
- Add profile selection, optional detailed mode, provider/model status and available usage totals.
- Keep factual support and insufficient-context responses distinct.

Acceptance:

- Each action uses the current snapshot and records its action/profile/model.
- An unclear question does not produce fabricated meeting facts.
- Cancelled/partial answers and unknown usage are labelled accurately.

Verification and evidence:

- Replay short technical and governance-style fictional meetings with rubric-based expected behavior.
- Test missing context, unsupported facts, profile changes and usage values absent from the provider.

Expected owned areas: `src/Sidecue.Application/Actions/`, `src/Sidecue.Desktop/Views/Overlay/`, `prompts/actions/`, `tests/Sidecue.Application.Tests/Actions/`.

### SC-019

**Select model defaults and validate assistance quality**

Phase: 2. Dependencies: SC-018. Required gates: G1.

Scope:

- Evaluate configured candidate model IDs against fixed synthetic meeting fixtures.
- Record latency/usefulness/cost metadata and select live/detailed defaults by evidence.
- Demonstrate cancellation, focus behavior and context continuity in an end-to-end meeting.

Acceptance:

- Selected models have a recorded configuration and rationale.
- Live/detailed profiles meet stated acceptance rubrics or limitations remain explicit.
- No stale-answer leakage occurs across request/session changes.

Verification and evidence:

- Measure capture/STT/retrieval/model/render stages separately with p50/p95 and test conditions.
- Perform a real Windows meeting demonstration and preserve a redacted evidence report.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `docs/evidence/SC-019/`, `tests/evals/`, `prompts/`, `configuration/model-profiles.json`.

### SC-020

**Import text documents with stable provenance**

Phase: 3. Dependencies: SC-004, SC-008. Required gates: none beyond task dependencies.

Scope:

- Implement bounded TXT/Markdown/DOCX/text-PDF extraction and chunking.
- Hash sources, version extraction and preserve page/heading/range references.
- Report unsupported scanned PDFs/legacy DOC without executing embedded content.

Acceptance:

- Malformed/oversized imports fail visibly without corrupting prior document versions.
- Equivalent source content has stable identity and chunks retain traceable locations.
- No document text leaks into logs or plaintext side indexes.

Verification and evidence:

- Fixture tests cover headings/tables, Unicode, empty/scanned documents, size limits and parse cancellation.
- Verify imported content and indexes are within encrypted storage.

Expected owned areas: `src/Sidecue.Infrastructure/Documents/`, `tests/Sidecue.Infrastructure.Tests/Documents/`, `tests/fixtures/documents/`.

### SC-021

**Create versioned embedding and keyword indexes**

Phase: 3. Dependencies: SC-020. Required gates: none beyond task dependencies.

Scope:

- Implement the embedding adapter and encrypted FTS/vector storage.
- Use restartable ingestion jobs and publish a new index generation atomically.
- Store provider/model/dimension/chunking metadata and reject incompatible search vectors.

Acceptance:

- Interrupted indexing never exposes a half-built document version.
- Changing an embedding model or dimensions triggers a separate rebuild.
- Cloud-indexing disclosure matches actual document text sent to the enabled provider.

Verification and evidence:

- Test restart/retry, duplicate imports, model/dimension mismatch, cancellation and atomic switch.
- Assert no plaintext FTS database or vector file is created.

Expected owned areas: `src/Sidecue.Infrastructure/Retrieval/Indexing/`, `src/Sidecue.Infrastructure/Embeddings/`, `tests/Sidecue.Infrastructure.Tests/Indexing/`.

### SC-022

**Retrieve evidence and validate citations**

Phase: 3. Dependencies: SC-014, SC-021. Required gates: none beyond task dependencies.

Scope:

- Combine FTS/BM25 and exact cosine ranking with reciprocal rank fusion.
- Filter to selected knowledge scope, deduplicate chunks and fit evidence into context budgets.
- Validate returned citation IDs and show source/version/location in the UI.

Acceptance:

- Relevant exact terms and semantic matches can both retrieve evidence.
- Deleted/out-of-scope chunks cannot enter prompts through caches.
- Invented citation IDs are rejected; missing support is visible.

Verification and evidence:

- Use a fixed corpus with expected retrieval cases, permission/scope exclusions, deletions and misleading evidence.
- Evaluate whether cited text supports claims, not only whether IDs exist.

Expected owned areas: `src/Sidecue.Application/Retrieval/`, `src/Sidecue.Infrastructure/Retrieval/`, `src/Sidecue.Desktop/Views/Evidence/`, `tests/evals/retrieval/`.

### SC-023

**Attach an approved screenshot to one request**

Phase: 3. Dependencies: SC-005, SC-015, SC-016, SC-017. Required gates: G0.

Scope:

- Connect selected-target capture and preview to the assistance request snapshot.
- Include only an explicitly confirmed image and honor provider vision capability.
- Bound and dispose image buffers after completion/cancel.

Acceptance:

- Cancelling preview never uploads the image.
- An image cannot be attached to a different request after cancellation or session change.
- Target closure, capture failure and unsupported vision fail with a usable text-only path.

Verification and evidence:

- Mock transport verifies image absence/presence by request ID.
- Real Windows tests cover target selection, preview, cancellation, resizing and no persistent screenshot artifacts.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Application/ScreenContext/`, `src/Sidecue.Desktop/Views/ScreenPreview/`, `tests/Sidecue.Application.Tests/ScreenContext/`, `docs/evidence/SC-023/`.

### SC-024

**Generate grounded meeting artifacts through durable jobs**

Phase: 3. Dependencies: SC-012, SC-014, SC-015. Required gates: none beyond task dependencies.

Scope:

- Transactionally enqueue post-meeting generation when finalizing a session.
- Generate summaries, decisions, open questions and action items with transcript references.
- Version generated drafts independently of user edits and make retries idempotent.

Acceptance:

- A failed/restarted job resumes without duplicating artifacts or overwriting edits.
- Unestablished owners/dates stay unassigned and incomplete transcripts remain identified.
- Generated facts refer to supplied finalized evidence.

Verification and evidence:

- Kill/restart around enqueue/claim/commit boundaries; inject generation failures.
- Evaluate unsupported claims, citation validity, missing owners/dates and regeneration after edits.

Expected owned areas: `src/Sidecue.Application/MeetingArtifacts/`, `src/Sidecue.Infrastructure/Jobs/`, `prompts/meeting-notes/`, `tests/Sidecue.Infrastructure.Tests/Jobs/`, `tests/evals/notes/`.

### SC-025

**Add editable notes and searchable meeting history**

Phase: 3. Dependencies: SC-022, SC-024. Required gates: none beyond task dependencies.

Scope:

- Provide history search, transcript navigation, editable note versions and evidence links.
- Export text/Markdown/transcript formats with explicit destinations.
- Keep large result lists virtualized and indexing off the dispatcher.

Acceptance:

- User edits survive regeneration, restart and export.
- Search results retain meeting/document provenance and do not mix generated claims with transcript quotes.
- Failed export cannot be reported as successfully written.

Verification and evidence:

- UI/integration tests cover edit/version selection, source navigation, large history and export failures.

Expected owned areas: `src/Sidecue.Desktop/Views/History/`, `src/Sidecue.Desktop/Views/Notes/`, `src/Sidecue.Infrastructure/Exports/`, `tests/Sidecue.Ui.Tests/History/`.

### SC-026

**Implement retention and deletion across derived data**

Phase: 3. Dependencies: SC-021, SC-024, SC-025. Required gates: none beyond task dependencies.

Scope:

- Implement explicit deletion and retention jobs covering transcripts, artifacts, chunks, vectors, FTS, queued jobs and app-managed backups.
- Invalidate relevant in-memory caches and prevent in-flight work from recreating deleted records.
- Explain that exported copies and provider-retained data are separate.

Acceptance:

- Deleted content cannot appear in search, prompt context, regenerated notes or queued work.
- Deletion is recoverable/retryable after interruption without dangling references.
- No secure-erasure or provider-deletion guarantee is invented.

Verification and evidence:

- Test deletion during generation/indexing and restart midway through a retention operation.
- Search every managed representation after deletion and validate cache invalidation.

Expected owned areas: `src/Sidecue.Application/Retention/`, `src/Sidecue.Infrastructure/Persistence/`, `tests/Sidecue.Infrastructure.Tests/Retention/`.

### SC-027

**Prepare editable follow-up drafts and complete grounded workflow**

Phase: 3. Dependencies: SC-023, SC-025, SC-026. Required gates: G2.

Scope:

- Generate an editable follow-up draft from selected meeting evidence and profile template.
- Support copy/export or draft handoff without automatic sending.
- Validate the full transcript-to-retrieval-to-notes-to-draft workflow.

Acceptance:

- Drafts preserve user corrections and do not invent commitments, owners or dates.
- No message is sent by generating or exporting a draft.
- The grounded workflow passes provenance and deletion checks end to end.

Verification and evidence:

- Use fictional meetings with disputed decisions and missing recipients/dates.
- Verify incomplete transcripts and deleted evidence cannot silently become confident commitments.

Expected owned areas: `src/Sidecue.Application/FollowUps/`, `src/Sidecue.Desktop/Views/FollowUps/`, `prompts/follow-ups/`, `tests/evals/follow-ups/`.

### SC-028

**Harden audio selection and speakerphone attribution**

Phase: 4. Dependencies: SC-013, SC-019. Required gates: G1.

Scope:

- Add process-tree audio selection with explicit system-audio fallback.
- Evaluate supported AEC capability and speakerphone duplication/overlap behavior.
- Document tested headset/speakerphone boundaries and keep anonymous diarization labels honest.

Acceptance:

- Process capture does not silently widen to all system audio.
- Browser process selection is not represented as guaranteed single-tab capture.
- Speakerphone limitations are measured and user-visible; text deduplication does not discard legitimate speech.

Verification and evidence:

- Physical tests cover speakers/headsets, multiple output devices, simultaneous speech and browser audio from multiple tabs.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Windows/Audio/`, `src/Sidecue.Application/Transcripts/`, `tests/Sidecue.Windows.Tests/Audio/`, `docs/evidence/SC-028/`.

### SC-029

**Complete device power and sharing compatibility checks**

Phase: 4. Dependencies: SC-017, SC-023, SC-028. Required gates: G0, G2.

Scope:

- Harden sleep/wake, lock/unlock, permissions, Bluetooth transitions, docking and GPU loss.
- Complete supported Teams/Zoom/Meet share-mode coverage on supported Windows Home/Pro builds.
- Update the compatibility matrix with observed limits and retest affected paths.

Acceptance:

- Resume/device transitions preserve session identity while marking new stream epochs and gaps.
- Unknown or failed screen exclusion never displays an unearned protected status.
- All declared supported configurations have observer-side evidence.

Verification and evidence:

- Run the physical Windows matrix across monitor/window sharing, popup/dialog cases, mixed DPI and monitor removal.
- Record exact versions and hardware alongside each result.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Windows/`, `docs/evidence/SC-029/`, `tests/Sidecue.Windows.Tests/`.

### SC-030

**Finish onboarding accessibility and language behavior**

Phase: 4. Dependencies: SC-025, SC-027. Required gates: none beyond task dependencies.

Scope:

- Add first-run provider/capture setup, clear cloud-processing disclosure and interactive diagnostics.
- Verify keyboard navigation, screen-reader labels, contrast and scalable text.
- Test explicit transcription/output language settings and per-language limitations.

Acceptance:

- A new user can establish working sources and providers without developer tools.
- Core meeting controls and history are operable with keyboard and assistive technology.
- Unsupported language/model combinations are prevented or clearly identified.

Verification and evidence:

- Run clean-profile onboarding and real screen-reader/keyboard checks.
- Use multilingual fixtures and expanded/long localized strings without clipping.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Desktop/Views/Onboarding/`, `src/Sidecue.Desktop/Resources/`, `tests/Sidecue.Ui.Tests/Accessibility/`, `docs/evidence/SC-030/`.

### SC-031

**Harden signed updates and release installation**

Phase: 4. Dependencies: SC-006, SC-012, SC-026. Required gates: G0.

Scope:

- Implement update checks and safe application only after the session stops.
- Verify signed release artifacts/update source validation and failed-update recovery.
- Check schema compatibility, downgrade handling, data preservation and explicit delete-data behavior.

Acceptance:

- An active meeting cannot be interrupted by automatic update/restart.
- An invalid or untrusted update is rejected.
- Clean install, upgrade, reinstall and uninstall behavior match documented data policy.

Verification and evidence:

- Run clean Windows standard-user install and real signed upgrade with interrupted download/apply scenarios.
- Record certificate/update trust behavior and ensure no credentials enter packages.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `tools/package.ps1`, `packaging/`, `src/Sidecue.Infrastructure/Updates/`, `docs/evidence/SC-031/`.

### SC-032

**Validate encrypted backup and restore boundaries**

Phase: 4. Dependencies: SC-004, SC-012, SC-026, SC-031. Required gates: none beyond task dependencies.

Scope:

- Implement consistent encrypted database snapshots and same-profile restore.
- Keep key/backups outside the installer root and label original-profile dependency.
- Defer portable passphrase export until independently designed and tested; do not imply DPAPI is portable.

Acceptance:

- A backup captured during activity restores a consistent committed state in the original usable profile.
- Wrong/missing key and lost-profile scenarios fail clearly without plaintext fallback.
- Restore cannot silently replace newer user data or bypass deletion/retention rules.

Verification and evidence:

- Restore snapshots from active WAL, after upgrade and after ordinary reinstall.
- Test absent/corrupt key and backup with an explicit rollback/recovery plan.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `src/Sidecue.Infrastructure/Backups/`, `src/Sidecue.Desktop/Views/Recovery/`, `tests/Sidecue.Infrastructure.Tests/Backups/`, `docs/evidence/SC-032/`.

### SC-033

**Run soak performance and privacy acceptance**

Phase: 4. Dependencies: SC-027, SC-028, SC-029, SC-030, SC-031, SC-032. Required gates: G3.

Scope:

- Run overnight synthetic/authorized meeting soak and measured performance scenarios.
- Inspect logs/temp/cache/crash behavior for raw audio, screenshots, keys and unintended plaintext.
- Exercise fault injection and address measurable regressions before release.

Acceptance:

- Non-history memory remains bounded, controls stay responsive, and measurements include test conditions.
- Transcript/provider/storage failures are visible and recoverable within documented limits.
- Captured content is absent from automatic diagnostics and app-created raw-media files.

Verification and evidence:

- Measure p50/p95 stage latency and compare to engineering targets with no unsupported marketing claims.
- Run disk-full, network faults, long transcript, deletion during work, cancellation and restart scenarios.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `tools/diagnostics.ps1`, `tests/Sidecue.Replay.Tests/`, `tests/evals/`, `docs/evidence/SC-033/`.

### SC-034

**Assemble personal release evidence and operating guide**

Phase: 4. Dependencies: SC-033. Required gates: G0, G1, G2, G3.

Scope:

- Compile the supported-environment matrix, resolved risks, remaining limitations and reproducible release checks.
- Verify shipped dependencies/notices, data lifecycle, model configuration and recovery instructions.
- Prepare reviewable release artifacts and release notes; public publishing requires its own authorized action.

Acceptance:

- Every claimed supported scenario points to actual evidence; blocked or untested scenarios remain explicit.
- All personal-release phase gates can be evaluated from recorded results.
- The owner can install/use/recover/update the reviewed build with the documented instructions.

Verification and evidence:

- Run a clean checkout release build and compare manifest/dependency versions to tested artifacts.
- Review package contents, signatures, notices and guide against observed behavior.
- This task requires interactive Windows evidence. A hosted build or mocked API cannot complete that requirement.

Expected owned areas: `docs/evidence/SC-034/`, `docs/RELEASE_READINESS.md`, `docs/USER_GUIDE.md`, `packaging/`.

## Deferred work

The deferred epic list in backlog.json retains the larger product scope. Do not begin these epics merely because a personal-release task is blocked. Calendar/briefs, hosted identity/sync/sharing, organizations/SSO/SCIM, CRM/ATS, billing/coaching, optional local inference and additional platforms each need their own detailed tasks when scheduled. EP-508 retains automatic insights and extended personal modes for a separately scheduled iteration after G2; these are not blockers for the first manual-assistance release.

The first release uses cloud speech/generation/embeddings with configured keys. A local capture path is not a claim of offline AI processing. Optional local inference and additional content converters are not required to complete the initial personal release.

## Evidence record minimum

For each real result, record task ID, commit/build identity, date, test command/procedure, expected and observed behavior, result, environment, remaining limitation and artifact location. Physical Windows checks also record OS edition/build, GPU/driver, device names, meeting client/browser version, sharing mode and observer method. Keep evidence free of secrets and private meeting content.

Use synthetic fixtures for ordinary automated tests. Live provider checks are explicit opt-in commands with configured credentials and known synthetic inputs, not part of default CI.
