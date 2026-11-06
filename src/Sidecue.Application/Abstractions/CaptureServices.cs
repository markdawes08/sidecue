using Sidecue.Core.Contracts;

namespace Sidecue.Application.Abstractions;

/// <summary>The session's fixed clock anchor and current typed time values.</summary>
public interface ISessionClock
{
    SessionClockAnchor Anchor { get; }

    /// <summary>Returns a nondecreasing position in Anchor.Origin's timeline, never before its origin.</summary>
    TimelinePosition GetPosition();

    /// <summary>Returns calendar time for history only; elapsed calculations use GetPosition.</summary>
    UtcTimestamp GetUtcNow();
}

public interface IAudioCaptureSource
{
    /// <summary>Opens one source and transfers ownership of the returned run to the session controller.</summary>
    /// <remarks>
    /// The adapter resolves the opaque selector and binds the request to its injected session clock,
    /// rejecting a different session or anchor. Cancellation covers opening only. After return, the run
    /// has an independent lifetime controlled by StopAsync, disposal and its read cancellation tokens.
    /// Opening failure or cancellation must release resources acquired before a run could be returned.
    /// </remarks>
    Task<IAudioCaptureRun> OpenAsync(CaptureRequest request, CancellationToken ct);
}

public interface IAudioCaptureRun : IAsyncDisposable
{
    /// <summary>Retains the final Paused, Stopped or Failed status even if the status stream is not read.</summary>
    CaptureStatusEvent? TerminalStatus { get; }

    /// <summary>Reads one consumer's owned audio frames; each yielded frame transfers ownership.</summary>
    /// <remarks>
    /// The consumer must dispose every received frame after use, including on failure or cancellation.
    /// The run owns and disposes untransferred frames, including on overflow and shutdown. Acquisition
    /// callbacks must not wait for consumers. Cancelling enumeration does not dispose the run itself.
    /// </remarks>
    IAsyncEnumerable<OwnedAudioFrame> ReadFramesAsync(CancellationToken ct);

    /// <summary>Reads a separate bounded/coalesced health and gap path, independent of audio queue capacity.</summary>
    /// <remarks>TerminalStatus must remain available after termination, independently of this enumeration.</remarks>
    IAsyncEnumerable<CaptureStatusEvent> ReadStatusAsync(CancellationToken ct);

    /// <summary>Idempotently stops acquisition and prevents new enqueues before successful completion.</summary>
    /// <remarks>
    /// Pause drops unsent audio and records the cutoff; End permits the configured bounded downstream
    /// drain; Recovery stops acquisition after a critical failure. Cancellation must not undo the stop
    /// request. DisposeAsync releases devices and untransferred buffers even if no enumeration began
    /// or StopAsync was cancelled. Neither operation may depend on a WPF dispatcher response.
    /// </remarks>
    Task StopAsync(CaptureStopReason reason, CancellationToken ct);
}

public interface IStreamingTranscriber
{
    /// <summary>Consumes and disposes transferred frames and yields normalized transcript events.</summary>
    /// <remarks>
    /// The adapter owns its connection and a bounded shutdown/drain. Cancellation stops new uploads
    /// and releases every frame it has received; the frame producer retains untransferred frames.
    /// Validate each frame against the request source, epoch, format and timeline. Maintain a sent-sample
    /// interval map across gaps: StreamStart alone cannot map later timestamps after dropped audio.
    /// Enumeration disposal must also release the connection and any currently held frames.
    /// </remarks>
    IAsyncEnumerable<TranscriptEvent> TranscribeAsync(TranscriptionRequest request,
        IAsyncEnumerable<OwnedAudioFrame> frames, CancellationToken ct);
}
