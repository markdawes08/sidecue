# Initial service and request contracts

SC-001B defines the interfaces in `Sidecue.Application.Abstractions` and their immutable envelopes in `Sidecue.Core.Contracts`. They use record schema **1**. Constructors reject unsupported versions and inconsistent metadata. Windows capture and normalization adapters exist separately; provider adapters and the session coordinator remain future work.

[SC-001C replay](REPLAY.md) supplies deterministic test-only implementations and executable reference observations for these boundaries.

## Mapping to the specification

| Conceptual contract in CONTRACTS.md | Compiled API and boundary |
| --- | --- |
| Capture source/run | [IAudioCaptureSource, IAudioCaptureRun](../src/Sidecue.Application/Abstractions/CaptureServices.cs) preserve Open, frame/status streams and Stop. `TerminalStatus` additionally retains final status independently of stream consumption. |
| Session clock | `ISessionClock` exposes an immutable anchor, typed current position and UTC history time. |
| Streaming transcriber | `IStreamingTranscriber.TranscribeAsync` consumes owned frames and returns normalized events. |
| Context assembler and assistance | [IContextAssembler, IAssistanceProvider](../src/Sidecue.Application/Abstractions/AssistanceServices.cs) retain Build/Generate signatures for the transcript-only snapshot below. |
| Session store | [ISessionStore](../src/Sidecue.Application/Abstractions/ISessionStore.cs) currently exposes CommitTranscript/ReadSession. End/summary enqueue and assistance artifact writes arrive with lifecycle/persistence work. |
| Knowledge, embeddings, screenshots and background jobs | Deferred to their feature tasks. There are no placeholder implementations or untyped extension fields. |

The broader signatures in [CONTRACTS.md](CONTRACTS.md) remain the target specification. Extending this subset must update the mapping and focused tests; do not silently treat an omitted feature as supported.

## Capture and lifetime

[CaptureRequest](../src/Sidecue.Core/Contracts/CaptureContracts.cs) freezes source identity, clock anchor, opaque selection and both duration/byte queue limits. The anchor replaces the conceptual live clock reference so Core does not depend on an Application interface. The adapter binds it to the injected `ISessionClock` and rejects a different anchor. Windows owns selector resolution; the selector is not a native handle, process ID or credential.

Open cancellation covers opening. The controller owns the returned run, even if enumeration never starts. Frame enumeration has one consumer: each yielded frame transfers ownership; the run owns untransferred buffers. Stop is idempotent; cancellation cannot undo its stop request. Disposal must release resources even after cancelled Stop or abandoned enumeration. The Windows capture and normalization implementations exercise these obligations through focused tests and limited local probes; this does not prove every device or shutdown scenario.

Windows `RecoveringAudioCapture` is a separate recovery owner above individual capture runs, not an `IAudioCaptureRun` with a single fixed epoch. It subscribes to a caller-owned endpoint monitor, accepts the explicit Multimedia `system-default` selector, and emits normalized frames and status records bearing their actual stream epochs on the original timeline. Reopen attempts are bounded, retain explicit loss and fail rather than silently reuse an old epoch. Its frame and status readers each have one consumer; transferred frame ownership remains with that consumer. The caller disposes the recovery owner before the monitor. A native open or stop that cannot finish promptly may require deferred cleanup. Physical device-change behavior remains a separate verification obligation.

Status events carry source/timeline identity, safe error categories and optional validated gaps. `TranscriptionRequest` pins language, format, source epoch, provider configuration reference and stream-start position. The transcriber must validate received frames, dispose them after use and maintain sent-sample mapping through gaps. A start offset alone is insufficient after dropped audio.

`Sidecue.Application.Timing.SentSampleMap` is the provider-independent helper for this mapping. Record a frame only after its send is confirmed. An uncertain send does not advance confirmed time and seals the map, so a new connection epoch requires a new map. The owner serializes access and supplies an interval capacity; reaching that capacity fails explicitly instead of discarding provenance. The helper is not a transport implementation.

## Commands and frozen context

[PauseListening, HideOverlay, ClearAssistantContext and EndSession](../src/Sidecue.Core/Contracts/SessionCommands.cs) are distinct versioned command types. They carry a session ID but perform no effects. Their XML comments preserve the documented semantics; the future controller allocates new context epochs and fences requests.

[AssistRequest](../src/Sidecue.Core/Contracts/AssistanceContracts.cs) fixes the answer fence, profile version, action/question, exact transcript cutoff, clear boundary, output profile, language and token budgets. `ContextSnapshot` retains that request and pins provider, model, configuration version and evaluated capability version. Configuration references contain identifiers only; adapters resolve protected credentials privately.

The initial snapshot accepts only this session's transcript after its clear boundary. It copies ordered finalized evidence/citation entries, an optional separately labelled partial revision and omission counts. It rejects foreign sessions/timelines, evidence newer than the cutoff, pre-boundary intervals and duplicate citation IDs. A segment spanning the clear boundary must be omitted until a later assembler can safely split supported evidence. Collection order is preserved; the assembler owns deterministic selection/order and token counting.

The assembler must use the exact cutoff or fail. Constructors cannot prove database provenance, token-budget compliance or citation grounding. Rolling summaries, document/index scope and approved images have no representation in this subset and are added in their planned tasks. Every generated event must preserve the snapshot fence; the future coordinator still rejects stale output after cooperative cancellation.

## Durable evidence

[TranscriptWrite](../src/Sidecue.Core/Contracts/TranscriptStoreContracts.cs) accepts only finals, amendments and gaps. A receipt means a committed transaction. Version zero denotes empty history; durable changes start at one. Identical retries return the original version as `NoOp`; conflicting reuse is a protocol error. Cancellation can race with commit, requiring a reread or retry using the same identity.

`SessionSnapshot` copies the full committed history in commit order, including earlier revisions. It checks session identity, unique event keys, increasing positive versions and a matching last/snapshot version. It is not a partial-text or current-final display projection. State reconciliation, canonical payload fingerprints and durable transaction behavior remain implementation responsibilities. Content-bearing envelopes use content-free `ToString()`; ordinary structured logging must still exclude their payloads.
