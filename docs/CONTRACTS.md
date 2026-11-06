# Sidecue application contracts

Contract baseline 0.1, November 6, 2026. This is an implementation specification, not compiled application code. The C# signatures below describe boundaries; agents must implement and test them in the solution. Read [DATA_AND_RECOVERY.md](DATA_AND_RECOVERY.md) for persistence and lifecycle behavior.

## Ownership and dependency direction

`Sidecue.Core` owns domain values and immutable records. `Sidecue.Application` owns use-case interfaces and orchestration. `Sidecue.Windows` and `Sidecue.Infrastructure` implement those interfaces. `Sidecue.Desktop` composes implementations and translates application events into view models.

No interface exposes a WPF control, HWND, provider SDK response, SQL connection, or API credential to Core. UI code does not call a provider or mutate the database directly. Keep these contracts in the existing five projects; do not create another assembly for every interface.

## Identity and time

Use small typed ID wrappers in the implementation rather than interchangeable strings.

| Value | Meaning and invariant |
|---|---|
| `SessionId` | One meeting record, generated before capture begins. Never reused for another meeting. |
| `TimelineEpochId` | A monotonic-clock origin valid within one application lifetime. |
| `SourceId` | Logical microphone or remote-playback source within a session. Device identity is separate. |
| `StreamEpoch` | Increasing integer for each open/reopen of a source transport or transcription connection. |
| `FrameSequence` | Increasing within `(SessionId, SourceId, StreamEpoch)`; counts audio frames, not provider events. |
| `SegmentId` | Stable logical transcript segment allocated by the normalized transcription adapter. |
| `SegmentRevision` | Increasing revision for one segment. Distinct from a session commit version. |
| `TranscriptCommitVersion` | Session-wide increasing version assigned atomically when durable transcript evidence changes. |
| `ContextEpoch` | Session-wide increasing generation used to invalidate assistant memory after Clear context. |
| `RequestId` | Unique assistance attempt. A retry with a fresh context gets a new ID. |
| `ProfileVersionId` | Immutable prompt/configuration version chosen for an assistance request. |
| `DocumentVersionId` | Immutable extracted source version. A changed source receives a new version. |
| `IndexGenerationId` | A complete compatible embedding/chunking generation. |

Persist calendar/history time as UTC `DateTimeOffset`. Use monotonic timestamps for capture ordering, elapsed time, queue age, and latency. Audio packets retain device/QPC timing where available and a normalized session offset in integer microseconds. Convert units at the Windows boundary; never compare raw clock tick values with different frequencies.

The session clock supplies `TimelineEpochId`, an anchor, and elapsed offsets. A device change creates a new stream epoch but need not reset the session clock. An application restart creates a new timeline epoch. Do not compare monotonic values across process lifetimes or infer a precise audio gap from wall-clock subtraction. Interrupted sessions preserve their old timeline; a later resumed session must explicitly record an estimated discontinuity or link a new session.

## Audio frame contract

`OwnedAudioFrame` has immutable metadata and one disposable buffer owner:

| Field | Requirement |
|---|---|
| Session/source/epoch/sequence | Full source identity, as defined above |
| Timeline epoch and start/end offsets | Ordered interval; duration agrees with sample count within documented rounding |
| Format | Sample rate, channel count, sample representation, and byte order |
| Flags | Silence, discontinuity, timestamp error, or ordinary data |
| Payload | `ReadOnlyMemory<byte>` valid only while its owner is alive |
| Sample count | Per-channel frame count; distinguish from bytes |

Capture begins in the device format. A normalization worker produces initial provider frames as mono 16 kHz PCM16 with approximately 100 ms payloads. The microphone and remote source stay independent. No early mixdown across sources.

The capture producer owns a buffer until it successfully transfers the frame into a queue. The consumer then owns it and must dispose it exactly once after use. Queue rejection, drop-oldest handling, cancellation, exceptions, and shutdown draining must all dispose abandoned frames. Any downstream operation retaining samples beyond the call must explicitly take ownership or copy them. Returning an array to a pool while a WebSocket send still references it is forbidden.

Bound audio queues by buffered duration/bytes, initially about two seconds per source. Do not block a capture callback. On overflow, remove oldest unsent audio, dispose it, and emit a coalesced `CaptureGap` with the dropped interval. Do not use a queue drop mode that loses the only opportunity to dispose or account for a frame.

## Capture and transcription interfaces

Conceptual signatures, with cancellation and lifetime semantics specified below:

```csharp
interface IAudioCaptureSource
{
    Task<IAudioCaptureRun> OpenAsync(CaptureRequest request, CancellationToken ct);
}

interface IAudioCaptureRun : IAsyncDisposable
{
    IAsyncEnumerable<OwnedAudioFrame> ReadFramesAsync(CancellationToken ct);
    IAsyncEnumerable<CaptureStatusEvent> ReadStatusAsync(CancellationToken ct);
    Task StopAsync(CaptureStopReason reason, CancellationToken ct);
}

interface IStreamingTranscriber
{
    IAsyncEnumerable<TranscriptEvent> TranscribeAsync(
        TranscriptionRequest request,
        IAsyncEnumerable<OwnedAudioFrame> frames,
        CancellationToken ct);
}
```

`CaptureRequest` contains source selection, device/process selection, session clock reference, and bounded-buffer policy. Native target handles stay inside the Windows implementation. A returned run is owned by the session controller. `OpenAsync` cancellation covers opening; after return the run has an explicit lifetime controlled by Stop/dispose and its read cancellation tokens.

`ReadFramesAsync` is single-consumer. The transcriber or a normalization stage consumes and disposes each transferred frame. Status events have a separate bounded/coalesced path so an audio overrun cannot erase its own failure notification. Critical terminal state is retained independently of a transient event queue.

`StopAsync` is idempotent. It stops further acquisition and prevents new enqueue operations before completing. Disposing a run must release native devices even if no enumeration started. Stop and disposal must not depend on the WPF dispatcher processing a response.

`TranscriptionRequest` contains source/epoch identity, language, format, provider configuration reference, and explicit stream-start mapping to the session clock. It does not contain a UI object. The provider adapter owns the network connection and bounded shutdown/drain behavior.

Provider timestamps describe the audio actually transmitted, not necessarily continuous captured time. Maintain an ordered sent-sample-to-session interval map per source/stream epoch using each normalized frame's sample count and original session interval. Convert provider offsets through this map. A start offset alone is insufficient after dropped audio. For example, after sending captured seconds 0 to 1, dropping 1 to 2, and sending 2 to 3, provider offset 1.25 maps to session offset 2.25. Queue overflow must not shift all later transcript timestamps backward.

If an event spans a discontinuity, retain the supported source interval list and explicit timing certainty; use word-level timestamps to split references when available. Do not manufacture exact timing through a missing interval. Unknown send completion on disconnect creates an uncertainty marker and new connection epoch. An alternative adapter may restart at each discontinuity with a new epoch, but must document and test that policy without replaying old audio silently.

## Normalized transcript events

Every event carries `ContractVersion`, `SessionId`, `SourceId`, `StreamEpoch`, a normalized `EventId`, and ordered `EventSequence`. Provider event identifiers may be retained as opaque provenance, never assumed globally unique.

| Event | Required payload and behavior |
|---|---|
| `PartialSegment` | Segment ID/revision, time interval, text, provisional speaker key. Replaces that segment's older partial. |
| `FinalSegment` | Segment ID/revision, mapped interval(s), timing certainty, text, source/speaker provenance. Enters the durable write path. |
| `FinalAmendment` | Higher revision plus the exact finalized revision it amends. Used only when adapter semantics explicitly support corrections. |
| `UtteranceBoundary` | Last relevant offset; does not itself finalize text. |
| `TranscriptGap` | Source interval, reason, and certainty. A missing interval is not invented text. |
| `StreamStatus` | Connecting, live, degraded, draining, stopped, or failed; safe error category. |

Deepgram `is_final` and `speech_final` represent different concepts. A final segment can arrive before the utterance boundary. The adapter must assemble a stable segment identity from that provider's protocol; arbitrary text equality or a rounded timestamp is not an acceptable deduplication key.

The transcript reducer applies these rules:

1. Duplicate `(source, epoch, EventId)` with the same payload fingerprint is a no-op.
2. Reuse of an event ID with different content is a protocol conflict and visible diagnostic, not silent overwrite.
3. A lower/equal partial revision cannot replace a newer one. A late partial cannot demote a finalized segment.
4. The first accepted final revision commits once. Replayed final revisions are idempotent.
5. A different final value requires an explicit amendment and preserves revision history.
6. Durable transcript changes receive a new `TranscriptCommitVersion` in the same transaction.
7. Sources can overlap. Order display by normalized start time, then source ID and segment ID for a deterministic tie-break. Do not impose false sequential speech.
8. Speaker keys are `(SourceId, StreamEpoch, ProviderSpeakerId)`. A speaker number after reconnect is not automatically the same person.

Keep provisional UI text separate from committed evidence. The UI can display a current partial immediately, but saved summaries and action items consume committed final revisions. An explicit live request may include the current partial only as labelled provisional context, with its revision captured in the request snapshot.

## Assistance and evidence interfaces

```csharp
interface IContextAssembler
{
    Task<ContextSnapshot> BuildAsync(AssistRequest request, CancellationToken ct);
}

interface IKnowledgeRetriever
{
    Task<IReadOnlyList<EvidenceChunk>> RetrieveAsync(
        RetrievalQuery query, CancellationToken ct);
}

interface IAssistanceProvider
{
    IAsyncEnumerable<AnswerEvent> GenerateAsync(
        ContextSnapshot snapshot, CancellationToken ct);
}

interface IEmbeddingProvider
{
    Task<EmbeddingBatch> EmbedAsync(EmbeddingRequest request, CancellationToken ct);
}

interface IScreenshotCapture
{
    Task<OwnedScreenshot?> CaptureForPreviewAsync(
        ScreenshotRequest request, CancellationToken ct);
}
```

`AssistRequest` specifies SessionId, RequestId, ContextEpoch, action, optional typed question, ProfileVersionId, requested transcript cutoff, output profile, selected knowledge scope, and optional explicitly approved screenshot reference. Provider/model configuration is resolved and pinned for the request before sending.

`ContextSnapshot` is immutable after assembly. It includes:

- Request/session/context IDs and exact committed transcript version used.
- Profile version and selected provider/model/capabilities/configuration version.
- Ordered source transcript references and an optional separately labelled partial revision.
- Rolling summary version with original segment references.
- Evidence chunks and selected index generation.
- Optional screenshot lease and approval record bound to that request.
- Input/output token budgets, language, and truncation/omission metadata.

`EvidenceChunk` includes citation ID, source type, document/segment version ID, page/heading/time range, text, access scope, and retrieval score metadata. Citation IDs are allocated by Sidecue and must be validated against the snapshot when returned by the model. Summaries and documents are untrusted evidence, not instruction or tool authority.

`RetrievalQuery` must include allowed scope and index generation. Never search unauthorized material and then attempt to hide it only after generation. Initial personal scope is simpler than a tenant model, but the interface must not imply that every stored document is always in scope.

Embeddings in one index generation must agree on provider, model, dimensions, normalization, and extraction/chunking version. Provider fallback cannot silently create mixed embeddings. Publishing a new generation is atomic after successful validation.

`OwnedScreenshot` is a disposable memory lease with target identity, capture time, dimensions, and content type. Preview does not authorize upload. Bind explicit Include/Send approval to the exact request and captured image, then transfer ownership to the request coordinator. Dispose on send completion, rejection, cancellation, timeout, or replacement. Never serialize image bytes into ordinary diagnostics or the database.

## Answer events and request fencing

`AnswerEvent` carries SessionId, ContextEpoch, RequestId, and increasing AnswerSequence. Event kinds are Started, TextDelta, EvidenceUpdated, Completed, Cancelled, and Failed. A terminal event is emitted at most once by the coordinator; adapters may fail without sending one, so the coordinator supplies terminal state.

The coordinator owns one foreground generation slot per active session. A new manual request atomically supersedes the previous request ID and cancels its token before starting the new operation. Automatic suggestions have lower priority and cannot starve manual requests.

Validate request fencing both before publishing UI updates and before accepting completion. A response is current only if its session, context epoch, and request ID still match the active slot. Cancellation is cooperative, so correctness cannot depend on the remote provider stopping immediately. Late deltas/completion are ignored. Record the old attempt as cancelled without appending late text to the new answer.

Clear context increments ContextEpoch, clears assistant rolling/context state, and cancels outstanding assistance. It does not delete durable meeting history. Subsequent context assembly must respect the clear boundary rather than silently reintroducing earlier text through a rolling summary or retrieval of that same session.

`Completed` includes assembled answer text, verified source references, provider/model ID, usage when supplied, and safe finish metadata. A failed/cancelled attempt can retain a clearly labelled partial artifact only under the configured retention policy. Never report it as a complete answer. Do not request or persist raw chain-of-thought.

## Durable interfaces

```csharp
interface ISessionStore
{
    Task<CommitReceipt> CommitTranscriptAsync(TranscriptWrite write, CancellationToken ct);
    Task<SessionSnapshot> ReadSessionAsync(SessionId id, CancellationToken ct);
    Task<EndReceipt> EndAndEnqueueSummaryAsync(EndSessionWrite write, CancellationToken ct);
    Task SaveAssistArtifactAsync(AssistArtifact artifact, CancellationToken ct);
}

interface IBackgroundJobStore
{
    Task EnqueueOnceAsync(JobDefinition job, CancellationToken ct);
    Task<JobLease?> ClaimNextAsync(JobQuery query, CancellationToken ct);
    Task CompleteAsync(JobCompletion completion, CancellationToken ct);
    Task RescheduleAsync(JobRetry retry, CancellationToken ct);
}
```

`CommitReceipt` means the transaction committed, not merely that an item reached a queue. It contains the assigned transcript commit version and applied/no-op result. Ambiguous commit failures are resolved through idempotent IDs and rereading state, not blind duplicated inserts.

`EndAndEnqueueSummaryAsync` atomically persists terminal session state, transcript completeness, and a uniquely keyed summary job. The summary job references a fixed transcript version and profile/template version. A retry cannot silently run on a different evidence snapshot.

Jobs use expiring leases and a fencing token. Completion from an old lease cannot overwrite a newer attempt. Cancellation/deletion tombstones are checked again before applying job output. Generated note versions and user-edited versions remain distinct.

## Errors and testing obligations

Use typed categories such as PermissionDenied, DeviceUnavailable, ProviderAuthentication, ProviderRateLimit, ProviderUnavailable, ProtocolConflict, StorageUnavailable, CorruptData, Cancelled, and UnsupportedCapability. Include a safe message, operation ID, retryability, and optional provider status code. Credentials and transcript bodies do not belong in exceptions or logs.

Retry only transient operations with bounded attempts/deadlines and jitter. An authentication failure, incompatible index, bad schema, or unsupported format requires correction. Do not switch to a different provider unless it is explicitly enabled.

Required contract fixtures cover partial replacement, final replay, amendment, reused ID conflict, overlapping sources, epoch reconnect, audio queue overflow/disposal and correct post-gap provider timestamp mapping, request supersession, context clearing, stale completion, and job-lease fencing. Test known outcomes with a fake clock and deterministic adapters. Provider integration tests add protocol realism; they do not replace domain contract tests.
