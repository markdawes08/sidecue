# Verification plan

This is the verification contract for the Sidecue personal Windows release. It translates the architecture baseline into observable behavior. No application test or hardware scenario has been run as part of preparing this reference package.

## Evidence and completion rules

- A task is complete only when its acceptance behavior is implemented and the required evidence exists. A successful build proves compilation, not microphone quality or screen-share exclusion.
- Record the actual command, revision, environment, result, and evidence location using [the evidence template](../templates/TEST_EVIDENCE.md).
- Use `pass`, `fail`, `blocked`, or `not-run` for a test result. Use `not-applicable` only with a written reason tied to declared scope. A skipped test is not a pass.
- Keep task progress in `tasks/backlog.json`. Test records describe runs; they do not replace the task ledger.
- If the environment cannot perform a Windows hardware test, record that limitation and continue independent work. Keep the affected gate pending.
- Rerun checks affected by a change. Expand testing when a failure, changed dependency, or unresolved concern warrants it. Preserve the evidence from failed runs and the linked corrective work.
- Fixtures contain synthetic or intentionally provided test material. Ordinary CI uses fake providers and does not need private meetings or paid API calls.

## Test layers

| Layer | What it proves | Where it runs | What it cannot prove |
|---|---|---|---|
| Domain and application unit tests | State transitions, revisions, ordering, budgets, cancellation, and policy behavior | xUnit with deterministic clocks and fakes | Actual OS capture behavior |
| Adapter contract tests | Provider event normalization, partial frames, retry decisions, and schema handling | Local fake HTTP/WebSocket endpoints | Live provider latency or availability |
| Storage and ingestion integration tests | Native encrypted database, transactions, migrations, retrieval, imports, and recovery | Windows test process using the shipped native dependency | Audio-device or remote-sharing behavior |
| Desktop integration and UI automation | Bindings, focus commands, keyboard paths, visible statuses, and window lifecycle | Interactive Windows desktop; FlaUI where useful | A remote participant's actual view without a receiving client |
| Provider smoke tests | Current configured API capabilities and live streaming behavior | Controlled integration run with configured test credentials | General quality across meetings |
| Windows compatibility tests | Devices, capture modes, multi-monitor behavior, packaging, and power transitions | Physical/interactive Windows test environments | Untested versions or capture clients |
| Replay evaluation and soak | Long-running state correctness, answer quality, bounded queues, and resource trends | Deterministic replay plus real Windows sessions | Guaranteed performance on all hardware |

The build task must establish actual solution/project names and checked-in PowerShell entry points. Until those exist, no sample build command in a design document is proof that a test ran. The initial scripts should provide build, unit/contract tests, integration tests, packaging, and an explicit interactive-test entry point with machine-readable results.

## Behavioral cases

| ID | Scenario | Required assertion |
|---|---|---|
| V-001 | Several partial revisions, one final, then duplicate final | UI replaces partial text; one durable finalized segment exists; duplicate delivery cannot duplicate notes |
| V-002 | Final segment without an endpoint event, then a later endpoint | All finalized text is retained; endpointing does not discard earlier segments or trigger duplicate turns |
| V-003 | Two independently clocked sources and overlapping speech | Source identity remains intact; ordering uses normalized monotonic time; anonymous speakers are not assigned names automatically |
| V-004 | Reconnection resets provider sequence/speaker IDs | A new stream epoch prevents identity collisions; the missing interval is visible |
| V-005 | Audio consumer is slower than capture | Queue duration remains bounded; discontinuity is reported; later provider timestamps map through sent-sample intervals to original session time; callback never waits on network or SQL |
| V-006 | New Ask, Clear context, or a different session while old output arrives | Superseded events fail the session/epoch/request check and never render into the new answer |
| V-007 | Pause listening during active transcription and generation | New audio capture/upload stops and unsent audio is dropped; the cutoff is recorded; already approved generation may finish; UI does not imply all network work stopped |
| V-008 | Hide overlay | Listening/session continues; reopening shows the same current session; Hide does not substitute for Pause or End |
| V-009 | End reaches the drain timeout | Session records an incomplete tail; partial text is not promoted to final evidence; summary can use committed text with the limitation |
| V-010 | Disk full, write lock, or writer failure | RecoveryRequired stops capture before the bounded unsaved-text buffer fills; unsaved text is visibly distinguished; error reporting does not depend only on the failed database |
| V-011 | Process killed during capture, summary enqueue, or summary execution | Committed transcript returns; incomplete session is identified; transactional job enqueue and idempotency prevent duplicate output |
| V-012 | Model retries after the user edits notes | User edits remain intact; retry produces the same idempotent result or a separate draft version |
| V-013 | Context exceeds the selected model budget | Token counting includes prompts, evidence, and reserved output; old context is compacted with source references; full saved transcript remains intact |
| V-014 | A document or transcript asks the model to expose keys or execute commands | Content remains evidence; it cannot grant permissions, alter provider allowlists, access keys, or invoke external actions |
| V-015 | Returned citation ID was never supplied | App rejects or marks the invalid citation; it does not invent a document link |
| V-016 | Supported citation attached to an unsupported claim | Evaluation detects the grounding failure even though the ID exists |
| V-017 | Document update, deletion, or index-generation change | Search, vector cache, FTS, and queued work stop using obsolete content; a failed rebuild preserves the last complete allowed generation |
| V-018 | Provider error or unsupported capability | Error is visible; credentials/malformed requests are not retried forever; fallback does not route to an unapproved vendor |
| V-019 | Missing owner or due date in a meeting | Generated action item leaves that field unassigned and links to supporting segments |
| V-020 | Screen target is removed, resized, or replaced | No silent first-monitor fallback; preview matches the selected valid target; capture resources are disposed |

## Database and recovery proof

The encrypted-storage spike must test the exact native binary that will ship. Record SQLCipher version, underlying SQLite version, package provenance, architecture, and relevant compile options. Confirm that the build includes the architecture baseline's required SQLite WAL fix or a documented backport.

1. Write a synthetic canary to every persisted content class: transcripts, notes, document chunks, FTS entries, embeddings metadata, and job payloads.
2. Reopen with the correct key and verify all records and search behavior.
3. Verify that opening with no key or a wrong key cannot read content. A connection string containing `Password` is not evidence of encryption.
4. Inspect database, WAL, temporary files, and application-managed backups for unintended plaintext canaries. Record what was scanned and the limitations of a byte scan. Test configured encryption behavior as well as absence of a string.
5. Interrupt transactions and each supported migration boundary. Reopen with integrity checks and verify that schema/data recover consistently.
6. Restore a consistent encrypted snapshot with its protected key in the original usable Windows profile. Verify the documented failure outside that profile. Do not advertise portable recovery until its separate key-export design is implemented and tested.
7. Test retention/deletion across records, search indexes, vectors, cache, jobs, and app-managed backups. Identify user exports as outside managed deletion.

## Quality evaluation

Maintain versioned synthetic meeting scenarios and expected evidence in the repository. Start with the six [evaluation seeds](../fixtures/README.md), then add protocol, lifecycle, timing and fault fixtures in the implemented harness. Include technical discussion, governance review, overlapping speakers, an unclear question, missing evidence, long history, a corrected statement, and malicious instructions embedded in source content. A changed prompt/model/configuration must run against the same baseline set.

Record response usefulness, unsupported claims, citation support, speaker attribution, terminology, missed/incorrect decisions, and action-item owners/dates. A reviewer should be able to trace each judgment to the fixture and output. Keep generated suggestions distinct from facts stated in the meeting. Model-based grading can assist; it is not the sole ground truth for factual support.

## Performance measurements

These are proposed engineering targets and are **unmeasured**. They are not release guarantees.

| Metric | Initial target | Definition |
|---|---|---|
| Pause/hide control response | Under 100 ms under ordinary load | Input event to acknowledged UI/capture control transition; measure actual capture cutoff separately |
| First useful partial transcript | p95 at or below 2 seconds | Captured speech timestamp to the first meaningful visible partial; report speech sample and source |
| First visible answer text | p95 at or below 3 seconds | Manual request accepted to first answer text with a warmed connection and selected live model; screenshot confirmation is measured separately |
| Long-session memory | Bounded application working set trend over two hours | Separate UI/context/audio buffers from intentional durable transcript growth; record peak and end-to-start trend |
| Queue pressure | Bounded by configured durations/sizes | Record occupancy, overflow count, dropped intervals, and UI degradation status |

Report p50/p95, sample count, warm/cold state, hardware, OS/build, provider/model/configuration, network conditions, and errors. Separate capture, transcription, retrieval, first model token, and render time. Do not drop failed or timed-out requests from reported results without counting them explicitly.

## Phase exit evidence

All phase gates start pending. Gate progress belongs in the backlog, with links to test records.

| Gate | Evidence required |
|---|---|
| Phase 0 | Windows audio and exclusion prototypes, explicit screenshot target proof, device transition evidence, encrypted native database proof, clean install/update data separation, and replay harness |
| Phase 1 | Two-hour live transcript run, pause/resume, duplicate/reconnect tests, visible gaps, disk/writer failure behavior, interruption recovery, and export |
| Phase 2 | Real meeting assistance path, keyboard/focus tests, immutable context, stale-event rejection, budgets, profile versions, and measured latency |
| Phase 3 | Import/retrieval provenance, index rebuild/deletion, screenshot permission/preview, editable notes, action-item grounding, and idempotent jobs |
| Phase 4 | Declared supported Windows matrix, accessibility, tested language combinations, update authenticity, clean-machine installation, backup limitations, and overnight soak |

A missing signing credential or physical test machine blocks the relevant release proof, not unrelated coding work. Preserve the limitation and the exact next verification action. Do not publish a release or expand its compatibility claims merely because unit tests passed.
