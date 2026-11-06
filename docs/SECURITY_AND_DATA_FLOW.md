# Security and data flow

This document defines boundaries that agents must preserve while implementing the personal Windows application. It is a design contract, not a claim that controls have been implemented or audited.

## Data destinations

| Data | Local lifetime | Permitted external destination | Required behavior |
|---|---|---|---|
| Microphone and playback PCM | Short bounded memory buffers | Configured speech provider while listening is active | Separate source identity; no intentional raw-audio disk files |
| Partial transcription | Volatile current revisions | May enter an explicitly requested live context if labelled provisional | Never silently promote a partial to finalized evidence |
| Final transcript | Encrypted database under the selected retention policy | Selected excerpts to the enabled generation provider | Durable source segments independent of prompt trimming |
| Document text | Encrypted chunks/indexes; original user file remains at its source | Configured embedding provider; retrieved excerpts to enabled generation provider | Explain cloud indexing; retain provenance and enforce selected knowledge scope |
| Embeddings and FTS indexes | Same encrypted storage boundary as their source | No additional service in the first release | No plaintext sidecar search index |
| Screenshot | Bounded request memory | Enabled image-capable generation provider after explicit inclusion | Exact selected-target preview; dispose frame/GPU resources; no background screenshot collection |
| Generated answers and notes | Encrypted artifacts with model/profile/evidence metadata | User-invoked export or later configured integration | Separate generated drafts from user edits |
| API credentials and database key | Current-user DPAPI-protected secrets | Credentials only to configured provider endpoints that require them | Never include secrets in prompt context, logs, installer, or repository |
| Diagnostics | Operational metadata with bounded retention | Optional explicit diagnostic export | No transcript, prompt, screenshot, audio, or credential bodies |

The first version has local capture and storage with cloud transcription, generation, and embedding operations. It is not fully offline. TLS and encrypted storage do not make processing end-to-end encrypted against a provider that receives plaintext. Provider retention/training terms depend on the actual configured service and account. No compliance certification follows from using these libraries.

## Trust boundaries

1. **Windows device to application.** Source selection, endpoint identity, permissions, timestamps, and capture state establish what the application acquires. A process-tree selection can cover unrelated browser tabs. The UI must state the actual scope.
2. **Untrusted content to model context.** Meeting speech, imported documents, OCR/text, screenshots, and provider-generated text are data. They cannot alter configured endpoints, retrieve credentials, grant tool permissions, or authorize sending email.
3. **Application to cloud provider.** Send only the content required for the enabled feature. A retry stays with the same approved destination; fallback to another vendor requires that vendor already be enabled by the user or organization.
4. **Application memory to durable storage.** Persist only approved artifact classes. Encrypt source text and all derived searchable forms. Record limits of memory secrecy, including OS paging and full-memory dumps.
5. **Installation to user data.** Use `%LocalAppData%/SidecueData` outside Velopack RootAppDir for database, protected keys, settings, and backups. Ordinary uninstall preserves this data. Explicit deletion has a separate documented scope.
6. **Future hosted organization boundary.** Before introducing shared knowledge, enforce tenant/member authorization at retrieval, content access, caches, sync, and writeback. Do not synchronize SQLite files or DPAPI-wrapped keys across devices.

## Concrete threats and required controls

| Risk | Required control | Verification |
|---|---|---|
| Source content instructs the assistant to reveal keys or run code | No secret material in context; typed application actions; no general code execution in the meeting runtime | Injection fixtures and inspection of outbound request bodies using synthetic canaries |
| Provider outage sends content to an unintended vendor | Explicit enabled-provider allowlist and capability check | Failure/fallback contract test with an unapproved endpoint |
| Transcript is encrypted but FTS/vector/temp data leaks | One encrypted persistence boundary, validated native library, controlled temporary files | Wrong-key read test, file inspection, indexed canaries, native-version evidence |
| Logging or crash tools export meeting content | Content-free structured logs; no automatic full-memory upload; previewable/redacted diagnostics | Canary scan of logs, exports, failure paths, and configured crash artifacts |
| User believes Pause listening stops every outbound operation | Precise control semantics and visible non-audio activity | Concurrent listening/generation/indexing test |
| Captured screen differs from the chosen target | Stable native target identity, preview, per-request inclusion, no implicit fallback | Equal-resolution monitor, target-close, resize, and cancellation tests |
| Captured overlay is exposed despite an API success | Empirical supported-client matrix and immediate Hide control | Receiving-client observation for every declared supported capture path |
| Update replaces/deletes transcripts or loads an unsafe schema | User data outside installer root; signed package/update validation; transactional versioned migrations | Clean upgrade, tamper, interrupted migration, uninstall/reinstall, and rollback tests |
| Database failure silently loses finalized text | RecoveryRequired, stopped acquisition, bounded labelled unsaved-text buffer | Disk-full/writer failure injection with a durable-error-path-independent UI |
| Imported document consumes excessive resources or executes content | File/size/page/time limits; passive parsers; no macros or embedded code | Malformed/oversized fixture suite; bounded resource metrics |
| Replayed/late events alter another meeting or answer | Session, stream epoch, segment revision, context epoch, request ID, and idempotency checks | Deterministic duplicate/out-of-order/cancellation fixtures |

## Secrets and credentials

Use current-user DPAPI for credentials and the randomly generated SQLCipher key. A successful encrypted database connection is a phase-zero proof requirement. Keep secrets out of public command arguments and outputs. Test fixtures use unmistakably synthetic values. A shipped desktop binary must not contain a service-owned master API key.

Validate configured provider endpoints and their credential scope. Never forward one provider's key when following an unexpected redirect or changing an endpoint. Local test endpoints must use an explicit development/test mode with synthetic credentials. Later local-model endpoints need a separate capability/configuration policy rather than weakening production HTTPS checks globally.

Initial backup recovery requires the original usable Windows profile and key material. Explain that limit at backup/restore. Portable recovery requires a separate supported encrypted export design; it cannot be inferred from copying a DPAPI-wrapped key.

## User control semantics

- **Pause listening:** stop acquisition and new audio uploads, discard unsent audio, and record the cutoff. An answer based on already approved context may continue. Separately requested embedding work may continue. Show non-audio activity independently.
- **Hide:** change visibility only. Listening and session state continue.
- **Clear assistant context:** cancel generation and increment the context epoch. Saved history is not deleted.
- **End session:** stop capture, bound the transcription drain, commit final segments, and enqueue idempotent notes. Mark unresolved tail intervals instead of treating partial text as final.
- **Delete history/document:** remove managed records, derived indexes, cache, jobs, and app-managed backup copies covered by the retention policy. User-exported files and provider copies are distinct and must not be claimed deleted automatically.

## Exclusion is a display feature

`WDA_EXCLUDEFROMCAPTURE` concerns supported OS capture paths. It is not a security boundary, an anti-detection mechanism, or a promise that all recordings will omit Sidecue. The process remains an ordinary Windows application. Do not claim that DirectX rendering alone makes content invisible, that Home is categorically unsupported, or that screen exclusion removes application traces from Task Manager.

## Release evidence

The first release needs evidence for storage encryption, credential handling, privacy controls, deletion scope, update authenticity, source/request isolation, and the declared capture matrix. Reference [the verification plan](VERIFICATION.md) and [Windows matrix](WINDOWS_TEST_MATRIX.md). Controls remain pending until their implementation and relevant tests exist. Routine implementation choices within these boundaries do not need repeated user confirmation.
