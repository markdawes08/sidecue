# Audio capture prototype

SC-003A adds separate shared-mode microphone and render-loopback sources behind the existing capture contracts. They are not yet connected to the shell, transcription, storage or a provider. Each request carries a separate source identity and resolves the explicit `system-default` selector to the Windows Multimedia default for the requested source kind. The loopback source is the selected endpoint's system mix and may include other applications; it is not isolated to a meeting process. The selected endpoint is fixed for that run; automatic device routing and reopen are later work.

The Windows adapter uses `NAudio.Wasapi` 3.3.0. It validates the actual endpoint mix format and rejects unsupported representations rather than relabeling bytes. The borrowed WASAPI callback span is copied into a bounded, owned buffer; the buffer is cleared and returned when the consumer disposes it. Callbacks do not wait for a consumer, the UI, network or storage. Both duration and byte limits apply. Overflow reports a gap; if the bounded status path cannot represent further loss, capture stops with an explicit uncertain failure. Native discontinuity flags are not assumed to mean device removal.

`OpenAsync` owns partial-open cleanup and uses a worker without a WPF synchronization context. The caller owns the returned run even if it never enumerates. A frame transferred by `ReadFramesAsync` belongs to the consumer, which must dispose it. Pause drops unsent frames; End allows a bounded drain; disposal releases abandoned buffers and the endpoint. Stop cancellation cancels the caller's wait, not the stop request. Final status remains available from `TerminalStatus` even when status events are not read.

Raw device positions and QPC positions are preserved when representable. Current session intervals are callback-time estimates and carry `TimestampError`; they are not a validated QPC-to-session-clock mapping. SC-003B tracks the required mapping and uncertainty work. An idle render endpoint may not emit loopback packets. Endpoint changes, reconnects, normalization and physical headset/device-transition validation remain later blocks.

## Local probe

```powershell
powershell -NoProfile -File tools/probe-audio.ps1
# Reuse an unchanged Release build:
powershell -NoProfile -File tools/probe-audio.ps1 -NoBuild
```

The probe opens both default endpoints briefly, renders synthetic silence to the selected Multimedia output, counts frames and disposes every lease. It does not write, log or upload audio samples. The metadata-only result is written under ignored `artifacts/audio-probe/audio-probe.json`. It reports frame counts, source/sequence checks, sample formats, raw-clock availability, timestamp uncertainty and terminal states. A nonzero exit indicates a failed probe; inspect the reported failure type where available rather than assuming a device or permission is available.

The probe may transiently access the microphone and system playback mix in memory. It does not prove speech quality, source isolation in a meeting, device-change recovery or any full audio compatibility case. See [SC-003A evidence](evidence/SC-003A/2026-11-06.md) for the actual run.
