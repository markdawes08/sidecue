# Deterministic contract replay

SC-001C adds a small executable reference model in [Sidecue.Replay.Tests](../tests/Sidecue.Replay.Tests/ReplayHarness.cs). It uses the compiled Core records and Application interfaces. Nothing in the desktop application references this test assembly.

Run it from the repository root:

```powershell
powershell -NoProfile -File tools/replay.ps1
# After an unchanged successful build:
powershell -NoProfile -File tools/replay.ps1 -NoBuild
```

The command runs seven focused replay tests. The main case runs [first-meeting.json](../tests/fixtures/replay/first-meeting.json) twice with fresh fakes, compares both normalized results and checks the fixture's explicit expected outcomes. It then writes `artifacts/replay/first-meeting-run1.json` and `first-meeting-run2.json`. A failure returns a nonzero exit code. The regular test suite and CI also include these tests.

## Fixture format

Fixture schema **1** maps to contract schema **1**. This is a test input format, not a provider protocol or persisted application format. All GUIDs, timestamps and text are fictional.

- The root fixes the fixture ID, session, timeline epoch and UTC anchor. No random IDs or system-clock reads determine results.
- Each stream declares `sourceKind`, source ID, stream epoch, captured start offset, frame count and transcript events in delivery order. Frames contain zero-filled mono 16 kHz PCM16 samples, 100 ms each. No audio is written to disk.
- Events declare kind, event ID, sequence and offsets in integer microseconds. Segments also declare segment ID, revision, text and optional anonymous speaker label; amendments name their old revision. Event delivery can be reordered without changing capture offsets.
- `invalidEvents` exercise constructor rejection, including empty IDs and negative revisions. Unsupported fixture versions fail before execution.
- Assistance steps request, clear or deliver an answer. Request IDs and context epochs stay fixed for each attempt. A scripted provider deliberately returns late output despite a cancelled token.
- `expected` lists exact per-step outcomes and final counts/text. Update expectations only when intended contract behavior changes; repeatability alone does not establish correctness.

## Reference behavior

The controllable clock advances to batch delivery time while retaining overlapping source intervals. UTC history time derives from the fixed anchor; elapsed positions stay typed and monotonic. Reopening the microphone creates a new stream epoch without resetting the session timeline.

Capture owns queued synthetic buffers, and the transcriber disposes transferred frames. Disposal releases unconsumed buffers even after cancellation. The finite capture fake rejects a script larger than its configured buffer budget; it does not simulate native callback overflow.

The transcript reference model compares explicit canonical fingerprints, not record equality or diagnostic text. Fingerprints include typed identity, event sequence, event kind, ordered intervals, text, certainty and speaker metadata. Logical revision comparison omits the event header so the same finalized revision under another delivery ID remains a duplicate. Conflicting reuse does not overwrite evidence. Explicit amendments retain the original revision in the fake store.

The in-memory store returns a positive commit version after accepting a write. Identical retries return the original version; conflicts throw. Its consistent snapshots retain final/amendment/gap history, while the reference display keeps partials and current finals separate. Final display order uses timeline, start offset, source and segment identity, with stream epoch breaking ties.

The answer driver retains each attempt's original snapshot and token. A replacement or Clear context cancels the old token. Late callbacks still arrive with the old fence and are rejected before visible text changes. Clear advances the context boundary and excludes old transcript evidence from the new snapshot without deleting committed history.

## Limits and next work

This proves the fixture/reference behavior only. The scripted transcriber consumes frames and emits prewritten events; it does not recognize speech or implement post-gap provider timestamp conversion. The store is sequential memory, not a durable transaction engine. The answer driver is not the production request scheduler, context assembler or lifecycle controller.

Native audio/overflow behavior, provider protocols, persistence recovery, job fencing, token budgets and production cancellation/concurrency remain in their planned tasks. Keep the reference rules explicit and small; implement production behavior in its owning layer and reuse the fixtures where appropriate.
