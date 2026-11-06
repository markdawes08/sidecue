# Sidecue data and recovery design

Baseline 0.1, November 6, 2026. These are implementation requirements and initial tuning values, not evidence that the application is built or tested. See [CONTRACTS.md](CONTRACTS.md) for event identities and interfaces.

## Persistent and transient data

Persist finalized transcripts, explicitly marked retained provisional tails when useful, gaps, meeting metadata, prompt/profile versions, assistance artifacts, notes, imported text, embeddings, job state, and configuration. Keep raw audio and screenshots transient in bounded memory. Do not create a hidden recording or screenshot cache to work around recovery limitations.

Operational logs contain identifiers, counts, timing, state, and safe errors. They exclude audio/image payloads, transcript/prompt bodies, document text, credentials, and raw chain-of-thought. OS paging and full-memory crash dumps are outside the promise of ordinary application storage. Disable content-bearing automatic crash uploads.

The authoritative transcript is the committed sequence of finalized segment revisions. Partial display text is a separate projection. Prompt context and rolling summaries are derived views and may be discarded/rebuilt without changing original evidence.

## Storage location and encryption

- Store user state under `%LocalAppData%\SidecueData`, outside Velopack `RootAppDir` and `AppContentDir`.
- Keep the database on a local filesystem, outside network shares and automatic sync folders.
- Ordinary uninstall preserves user data. An explicit delete-data operation removes app-managed data.
- Generate a random database key using the platform cryptographic RNG. Protect it with current-user DPAPI and user-restricted file ACLs.
- Protect provider credentials independently with current-user DPAPI. Never place live secrets in source, packaged `.env` files, logs, or command-line arguments.
- Use `Microsoft.Data.Sqlite.Core` with a maintained, distributable, tested SQLCipher native build. Record the actual native versions and dependency/license evidence.
- Validate that the database, WAL/journals, FTS indexes, embeddings, temporary files, and backups do not expose plaintext under the chosen configuration.
- A connection-string Password field with ordinary SQLite does not establish encryption. Wrong-key and ordinary-SQLite-open tests must fail to reveal content.

The exact SQLCipher distribution is an initial feasibility decision. Do not replace the requirement with plaintext storage to finish a task. A missing compatible native build blocks production persistence while fake/in-memory contract tests can continue.

Use foreign keys, a serialized writer, short read transactions, WAL, and `synchronous=FULL` initially. Verify that the native SQLite build contains the documented WAL-reset fix, such as 3.51.3 or a documented backport. Version claims refer to the loaded native library, not only the managed package.

## Logical schema

This table specifies logical fields and constraints. Migration authors choose concrete SQL representations and test them. IDs must have one canonical encoding. UTC timestamps, integer offsets, and schema versions must not depend on current locale.

| Table | Essential columns | Key constraints and indexes |
|---|---|---|
| `sessions` | id, title, state, started_utc, ended_utc, transcript_version, context_epoch, context_start_offset, completeness, profile_version_id, language, retention_policy | PK id; index started_utc; transcript_version/context_epoch never decrease |
| `timeline_epochs` | id, session_id, anchor_utc, clock_kind, clock_frequency, relation_to_previous | PK id; FK session; unique session/id; previous relation may be estimated |
| `audio_streams` | id, session_id, source_id, stream_epoch, timeline_epoch_id, kind, device_reference, started_offset, ended_offset | unique session/source/epoch; index session/start offset |
| `transcript_segments` | id, session_id, source_id, stream_epoch, ordinal, current_final_revision | PK id; unique session/source/epoch/ordinal |
| `transcript_segment_revisions` | segment_id, revision, start_offset, end_offset, timing_ranges_json, timing_certainty, text, speaker_key, finality, event_id, payload_hash, commit_version | PK segment/revision; unique normalized event identity; index session evidence through segment/commit |
| `transcript_gaps` | id, session_id, source_id, stream_epoch, start_offset, end_offset, reason, certainty, commit_version | PK id; index session/start; end >= start when known |
| `speaker_assignments` | session_id, source_id, stream_epoch, provider_speaker_id, display_name, assignment_origin | unique scoped speaker key; do not globally merge diarization IDs |
| `assist_requests` | id, session_id, context_epoch, transcript_version, profile_version_id, action, provider, model, config_version, status, started_utc | PK id; index session/time |
| `assist_artifacts` | request_id, revision, status, text, evidence_json, usage_json, finished_utc | PK request/revision; completed/cancelled/failed explicit |
| `documents` | id, logical_source, display_name, selected_scope, active_version_id, deleted_at | PK id; index scope/deleted; no credentials in source URL |
| `document_versions` | id, document_id, source_hash, extractor_version, extracted_utc, state | PK id; index document/hash; immutable completed version |
| `index_generations` | id, provider, model, dimensions, normalization, chunker_version, state, activated_utc | PK id; publish only a fully validated generation |
| `document_chunks` | id, document_version_id, index_generation_id, ordinal, location_json, text, embedding_blob | PK id; unique version/generation/ordinal; dimensions validated |
| `document_chunks_fts` | chunk_id and searchable text fields | encrypted database FTS5 table; sync in same transaction as chunks |
| `prompt_profile_versions` | id, profile_id, version, behavior, note_template, followup_template, output_schema, created_utc | unique profile/version; versions immutable |
| `meeting_artifact_versions` | id, session_id, kind, origin, source_transcript_version, profile_version_id, content, evidence_json, parent_version_id | PK id; generated and user-edited origins separate |
| `background_jobs` | id, kind, idempotency_key, payload, state, attempts, next_attempt_utc, lease_token, lease_until_utc, last_safe_error | unique idempotency_key; index state/next_attempt |
| `deletion_tombstones` | target_type, target_id, deleted_utc, reason | unique target type/id; consulted by late jobs and future sync |
| `schema_versions` | version, checksum, applied_utc, app_version | PK version; checksum detects altered migration history |

Foreign keys prevent orphaned segments, artifacts, and chunks. Cascades must be intentional and covered by deletion tests. A document's active version and search generation must remain consistent. Never expose a partly indexed document as fully searchable.

Add a persistent provider-event dedup table only if revision uniqueness cannot provide the needed idempotency. Do not store every high-frequency partial event indefinitely. Provider IDs may collide across reconnects, so uniqueness includes the normalized source and epoch.

## Write discipline and evidence snapshots

The database writer serializes write commands and acknowledges only after commit. It does not accept unlimited pending work. A successful queue insertion is not a Saved indication. UI state distinguishes pending, committed, and unsaved text.

Final revision insertion, dedup identity, transcript version increment, and derived-index changes occur in one transaction where applicable. Duplicate retries return the existing outcome. A different payload for an existing immutable revision is an error.

A request records its exact committed transcript version. Retrieval and summaries read a consistent evidence snapshot, not a moving combination of earlier and later rows. A later transcript amendment does not alter the evidence that produced an old answer; it can mark that answer as based on an earlier version.

Use prepared statements for values. Do not construct SQL from filenames, model output, or user text. Escape/validate FTS query syntax separately from SQL parameterization. Avoid long transactions during provider calls, document parsing, or user interaction.

## Session and processing state

Session lifecycle is distinct from capture health, generation state, and window visibility.

| Session state | Allowed behavior |
|---|---|
| `Idle` | No active session. Start may allocate and persist a new session. |
| `Starting` | Validate selected devices/provider settings/storage and open resources. Capture is not reported live until ready. |
| `Listening` | Capture enabled; individual sources can be healthy or degraded. |
| `Paused` | Listening intentionally paused; existing text and explicit manual assistance remain available. |
| `Stopping` | No new capture; bounded transcription drain and durable finalization in progress. |
| `Completed` | Normal session ended; completeness may be complete or incomplete. Background outputs can still run. |
| `Interrupted` | A prior active session did not end cleanly. Committed evidence remains available. |
| `RecoveryRequired` | Persistence/integrity failure requires explicit recovery before capture resumes. |

Nominal transitions are Idle -> Starting -> Listening, Listening <-> Paused, and Listening/Paused -> Stopping -> Completed. Opening failure returns to a safe non-listening outcome with an error. Critical persistence failure moves an active session to RecoveryRequired immediately. Startup detection of an unclosed prior session marks it Interrupted when storage is writable.

Capture has per-source Connecting, Live, Degraded, Paused, and Stopped status. Generation has Idle, BuildingContext, Streaming, Completed, Cancelled, and Failed status. Hiding the overlay changes neither state machine. Do not encode every combination as a new session state.

## User command semantics

| Command | Exact effect |
|---|---|
| Start | Persist session identity/settings and begin capture only after required resources are ready. |
| Pause listening | Stop audio acquisition and new audio uploads; drop unsent buffered audio; record cutoff/pause interval. |
| Resume listening | Reopen source connections with new epochs, retain session history, and end the intentional pause interval. |
| Hide overlay | Hide UI surface only. Capture, current assistance, and background work continue. |
| Clear assistant context | Increment ContextEpoch, set a context boundary, cancel generation, and clear derived assistant memory. Preserve saved transcript/history. |
| Ask | Use explicitly chosen action/context; can operate on existing text while listening is paused. |
| Include screenshot | Select/capture/preview and approve that image for that request. No continuous screen collection. |
| End session | Fence new manual requests for the active meeting, cancel active foreground generation, stop capture, drain/finalize, and enqueue summary once. |
| Delete meeting | Cancel related work, tombstone, and remove app-managed meeting data/derived indexes according to deletion policy. |

Pause listening does not cancel an answer already using approved context or separately requested embedding/indexing jobs. Those activities remain visible. Provider bytes already transmitted cannot be recalled. Final transcript messages for already-transmitted audio may arrive after Pause and can be committed only within that source's established cutoff; they do not permit new acquisition.

Clear context applies to recent text, rolling summaries, and same-session retrieval, so pre-clear content is not silently reintroduced. A user may explicitly select historical material later; that is a new context choice. The full transcript remains available in history.

End session is idempotent. An assistance request that races with End must either be accepted before the end fence and then cancelled, or rejected with a clear ended-session result. A separate post-meeting question can be created after finalization.

## Storage failure and bounded recovery

On disk-full, prolonged writer blocking, corruption, or an ambiguous unrecoverable write error:

1. Enter RecoveryRequired immediately and publish an in-memory error/status directly to the UI.
2. Stop audio acquisition and new audio uploads before unsaved transcript buffers can expand.
3. Cancel generation based on unsaved text and prevent background jobs from adding writes.
4. Retain a bounded unsaved-text buffer, initially 1 MiB including encoded text and serialized metadata. Bound object count too; the implementation must account for managed allocation overhead separately.
5. Label retained text as Unsaved. Offer a retry after the storage issue is resolved or an explicit user-selected export destination.
6. Apply retry through stable event/segment IDs. Read state after an ambiguous commit before duplicating work.
7. Resume only after persistence succeeds and resources are reopened with new source epochs.

Reserve capacity for source shutdown status and any already-returned final messages. If that reserve is exhausted, stop consuming/close the provider stream, flag transcript completeness unknown/incomplete, and report the loss explicitly. Do not claim the system can always preserve arbitrary incoming text when both disk and bounded memory are unavailable.

A database failure cannot reliably be logged to that database. In-memory status and content-free process diagnostics provide the fallback. Unsaved text is lost if the process terminates before successful retry/export; state that accurately.

## Normal end and unfinished tails

At End, record the last captured offset per source, stop acquisition, and signal provider finalization. Drain only until a configured deadline. The initial deadline must be documented and tested, not an unbounded wait.

Commit returned final segments. If the deadline expires, retain any available tail only as ProvisionalTail and record an unresolved interval up to the capture cutoff. The interval means potentially unfinished transcription, not proof that every sample in it contained speech. Set completeness to Incomplete or Unknown with the cause.

Atomically commit the terminal session state, final transcript version/completeness, and a summary job with an idempotency key based on session, transcript version, template version, and output kind. A session can be Completed with an incomplete transcript. The UI must present both facts.

Summary generation uses committed finalized evidence and reports relevant incompleteness. It does not silently treat a provisional tail as confirmed speech. If users later correct or confirm text, create an explicit revision and a new generation request.

## Crash and restart recovery

On startup, open and validate the encrypted store before starting capture. Find sessions left in Starting, Listening, Paused, or Stopping and mark them Interrupted when possible. Do not automatically resume microphone access after a crash.

Display committed transcript and its last known source offsets. Cancelled/unfinished assistance remains labelled accordingly. Reclaim expired job leases, then retry idempotent jobs. A missing summary job for an interrupted session is created only through the recovery action that selects the fixed transcript/template version.

No audio file exists to replay. Committed text can be recovered; audio after the last committed text and unsaved in-memory changes cannot. Resume may create a linked new session or an explicit new timeline epoch with an estimated interruption. Never infer clock continuity across restart.

Test process termination during a transcript transaction, after transcript commit but before UI acknowledgement, during End, during summary generation, and during migration. Recovery must not duplicate finalized segments or generated outputs.

## Background jobs and editable outputs

Jobs contain immutable input references and a unique idempotency key. A worker claims with a lease token, updates bounded attempts/backoff, and completes only if its lease is still current. Retry network/rate-limit errors within policy; invalid credentials or incompatible schemas become actionable failures.

Job output application rechecks deletion tombstones, input versions, and permissions. A late summary for a deleted meeting is discarded. A reindex job may publish only its complete generation. A cancelled document import cannot repopulate a deleted source.

Store generated note drafts and user edits as separate versions with parent references. Retrying a generation may return the existing draft, but must never overwrite a user-edited version. Suggested owners/dates stay unassigned unless source evidence supports them. Preserve cited transcript segment revisions with decisions/action items.

## Migrations, updates, and backups

Migrations are ordered, checksummed, and tested from each supported prior schema. Run them before capture, with a verified backup where required. Use transactions for changes that support them and explicit recovery for changes that do not. Do not edit an already-shipped migration to repair a later database.

An older application must refuse to write an unsupported newer schema. Installer rollback is not automatically database rollback. Declare the compatible schema range and test the selected update/recovery path before release.

Use a consistent encrypted snapshot/backup API while WAL is active; copying only the main database file is unsafe. Verify restored schema, encryption, integrity, and required key material in a controlled test. Never create a plaintext intermediate backup.

Initial backups are usable only with the original viable Windows user profile and its DPAPI-protected key. They do not recover data after profile/key loss and are not portable to a new computer. Label this limitation. A future portable recovery export requires a separately designed password-protected key/data format and tested maintained cryptographic implementation.

Keep app-managed backups outside the installer-owned directory, under the same access and retention policy as primary data. Ordinary uninstall preserves them. Deletion/retention must cover backup content through expiry or recreation; a deleted meeting remaining indefinitely in unmanaged backup snapshots violates the declared policy.

## Retention and deletion

Deletion first cancels work and creates a durable tombstone, then removes related text, artifacts, FTS rows, vectors, caches, source references, and jobs in bounded transactions. A retry completes unfinished deletion idempotently. Delete cached matrix entries before answering subsequent retrieval requests.

Record the scope of app-managed backups and exports. User-selected external exports are outside automatic deletion and must be identified as such. Local deletion does not imply provider-side deletion. Do not promise physical secure erasure of SSD blocks, old external copies, OS backups, or data already sent to a provider.

## Acceptance evidence

Required evidence includes wrong-key/plain-SQLite-open failure, encrypted WAL/temp inspection with synthetic canary text, restored snapshot integrity, migration interruption, old-schema/new-schema compatibility, disk-full and locked-writer behavior, duplicate final replay, End timeout completeness, late cancelled job output, edited-note preservation, index generation switching, and deletion across FTS/vectors/backups.

Use synthetic content and deterministic provider events in CI. Native Windows DPAPI, installed paths, real device transitions, and update/uninstall behavior need Windows tests. Record actual results and unresolved limits; this document itself does not satisfy those gates.
