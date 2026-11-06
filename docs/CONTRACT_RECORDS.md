# Implemented contract records

SC-001A implements values and records in [Sidecue.Core.Contracts](../src/Sidecue.Core/Contracts/IdentityValues.cs). [CONTRACTS.md](CONTRACTS.md) remains the behavioral specification. These types validate individual inputs; they do not implement capture, reconciliation, persistence or request coordination.

## Identity and time

- GUID-backed IDs reject empty values. Because C# permits default structs, enclosing records also reject default IDs. IDs have no implicit string/Guid conversion.
- Stream epochs, sequences, revisions, commit versions and context epochs are distinct nonnegative, zero-based values. Their future owners must enforce uniqueness, increasing values and checked increments across events.
- `ContractVersion.Current` is schema **1**, independent of architecture baseline **0.1**. Audio metadata, capture gaps and event headers reject default or unsupported versions. Future wire decoders must validate before constructing records.
- `UtcTimestamp` normalizes calendar/history values to UTC. `TimelinePosition` combines a `TimelineEpochId` with a `SessionOffset` in integer microseconds. `TimelineInterval` is half-open, ordered and confined to one epoch; zero-length intervals are allowed. Device changes may advance the stream epoch without changing the timeline epoch.
- Optional `RawClockPosition` values on audio metadata preserve device/QPC ticks with their frequency. Conversion and clock anchoring belong at the Windows boundary; raw values with different frequencies must not be compared directly.

## Audio ownership

`AudioFrameMetadata` holds the version, `SourceIdentity`, frame sequence, timeline interval, format, sample count and flags. The count is per channel. `PayloadByteCount` includes all channels and rejects integer overflow. An interval's duration must equal the floor or ceiling of `sampleCount * 1,000,000 / sampleRate`, allowing only integer-microsecond rounding.

`OwnedAudioFrame` is a disposable class, so copying a record cannot duplicate its ownership. Construction adopts the `IMemoryOwner<byte>` only after validation succeeds. On failure, the caller still owns the buffer. The payload excludes unused rented capacity. Repeated disposal releases the owner once; later payload access throws.

The producer owns the frame until a successful transfer; the consumer then disposes it after use. Rejection, cancellation and exceptions require cleanup by the current owner. Previously obtained memory views cannot be revoked: do not retain one after disposal or dispose while an asynchronous send still uses it. The focused tests demonstrate caller cleanup; queues and shutdown draining remain future work.

## Event mapping

`TranscriptEventHeader` carries the version, source identity, event ID and sequence. Its six variants map directly to the normalized transcript event table. Final intervals are copied into immutable collections and must be ordered, nonoverlapping and within one timeline. An amendment names the older revision and must advance it. A speaker key is scoped to its source and stream epoch.

`AnswerEventHeader` carries the version, `AnswerFence` (session, context epoch, request ID) and answer sequence. Its six variants preserve that fence. Citation IDs are opaque references to a future frozen context; constructing `AnswerCompleted` does not verify grounding. The future coordinator must resolve citations and reject stale results.

Collection-bearing records are immutable, but record equality is **not a payload fingerprint**: `ImmutableArray` equality can reflect collection identity. Replay/reconciliation must compare canonical payloads when checking duplicate event IDs. Content-bearing events have content-free `ToString()` output; this does not authorize structured logging or serialization of their text.

[SC-001B service/request contracts](CONTRACT_SERVICES.md) build on these records. Their durable store receipts use positive commit versions, reserving zero for empty history. [SC-001C replay](REPLAY.md) exercises the contracts through deterministic fakes. [Core tests](../tests/Sidecue.Core.Tests/Contracts/EventContractTests.cs) cover the record invariants using synthetic inputs.
