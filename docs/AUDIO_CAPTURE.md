# Audio capture prototype

SC-003A adds separate shared-mode microphone and render-loopback sources behind the existing capture contracts. They are not yet connected to the shell, transcription, storage or a provider. Each request carries a separate source identity and resolves the explicit `system-default` selector to the Windows Multimedia default for the requested source kind. The loopback source is the selected endpoint's system mix and may include other applications; it is not isolated to a meeting process. The selected endpoint is fixed for that run; automatic device routing and reopen are later work.

The Windows adapter uses `NAudio.Wasapi` 3.3.0. It validates the actual endpoint mix format and rejects unsupported representations rather than relabeling bytes. The borrowed WASAPI callback span is copied into a bounded, owned buffer; the buffer is cleared and returned when the consumer disposes it. Callbacks do not wait for a consumer, the UI, network or storage. Both duration and byte limits apply. Overflow reports a gap; if the bounded status path cannot represent further loss, capture stops with an explicit uncertain failure. Native discontinuity flags are not assumed to mean device removal.

`OpenAsync` owns partial-open cleanup and uses a worker without a WPF synchronization context. The caller owns the returned run even if it never enumerates. A frame transferred by `ReadFramesAsync` belongs to the consumer, which must dispose it. Pause drops unsent frames; End allows a bounded drain; disposal releases abandoned buffers and the endpoint. Stop cancellation cancels the caller's wait, not the stop request. Final status remains available from `TerminalStatus` even when status events are not read.

Raw frames preserve device and QPC positions when representable. When the session clock explicitly provides a QPC origin and the packet timestamp passes validation, the adapter maps its native QPC time onto the session timeline. Otherwise it falls back to callback-time estimation. Both are labeled `SessionTimestampEstimated`; `TimestampError` remains the separate native flag. The mapping is an estimate, not proof of exact capture time.

SC-003B adds `AudioNormalizationRun`, a worker that wraps a capture run and produces independent mono 16 kHz PCM16 frames for each source. It downmixes and resamples supported endpoint formats, normally in 100 ms chunks. Its output queue has explicit duration and byte limits; dropped frames are disposed and reported as gaps. It resets across discontinuities, does not fabricate filter-tail samples, and fails closed when source timing cannot be represented safely. Input and output buffers are cleared on disposal. The separate status path is bounded; a terminal failure is retained if it cannot account for further loss.

The normalizer accepts little-endian PCM16/24/32 or float32 at 8–192 kHz with up to eight channels. If estimated packet intervals overlap materially, it discards the affected packet and reports an unknown gap instead of assigning it invented timing. The resampler's private managed scratch storage cannot be explicitly cleared through its public API and may remain in process memory until reclaimed; OS paging and memory dumps are outside this cleanup guarantee. No audio file is intentionally created.

The platform-independent `SentSampleMap` records confirmed transport sends, not queued audio. Its caller sets a maximum number of timing intervals and serializes access. It maps provider sample offsets to supported source intervals across drops; an uncertain send seals the map and requires a new connection epoch. These components are not yet wired to a transcription provider. An idle render endpoint may not emit loopback packets. Endpoint changes, reconnects and physical headset/device-transition validation remain later blocks.

## Local probe

```powershell
powershell -NoProfile -File tools/probe-audio.ps1
# Reuse an unchanged Release build:
powershell -NoProfile -File tools/probe-audio.ps1 -NoBuild
# Exercise normalization using the same local endpoints:
powershell -NoProfile -File tools/probe-audio.ps1 -Normalize
```

The probe opens both default endpoints briefly, renders synthetic silence to the selected Multimedia output, counts frames and disposes every lease. It does not write, log or upload audio samples. The metadata-only result is written under ignored `artifacts/audio-probe/audio-probe.json`, or `artifacts/audio-normalization-probe/audio-probe.json` with `-Normalize`. It reports frame counts, source/sequence checks, sample formats, clock metadata applicability and uncertainty, gaps and terminal states. Normalized frames do not carry raw device/QPC positions; those belong to the original packets. A nonzero exit indicates a failed probe; inspect the reported failure type where available rather than assuming a device or permission is available.

The probe may transiently access the microphone and system playback mix in memory. It does not prove speech quality, source isolation in a meeting, device-change recovery or any full audio compatibility case. See [SC-003B evidence](evidence/SC-003B/2026-11-06.md) for the actual run.
