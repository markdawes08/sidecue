using Sidecue.Core.Contracts;

namespace Sidecue.Application.Sessions;

public enum SessionLifecycleState { Idle, Starting, Listening, Paused, Stopping, Completed, Interrupted, RecoveryRequired }
// Capture is an aggregate lifecycle status. Per-source health belongs to the capture owner.
public enum SessionCaptureState { Stopped, Connecting, Live, Degraded, Paused, Failed }
public enum SessionGenerationState { Idle, BuildingContext, Streaming, Completed, Cancelled, Failed }

/// <summary>An immutable, content-free view of the current session.</summary>
public sealed record LifecycleSnapshot(
    SessionId SessionId,
    SessionLifecycleState State,
    SessionCaptureState Capture,
    SessionGenerationState Generation,
    ContextEpoch ContextEpoch,
    TimelinePosition ContextStart,
    bool OverlayVisible,
    AnswerFence? ActiveRequest,
    EventErrorCategory? Error);

/// <summary>Whether the bounded end drain finished without an unresolved tail.</summary>
public sealed record DrainOutcome(bool Complete);

/// <summary>A not-yet-active capture lease. The caller owns it even when opening finishes late.</summary>
/// <remarks>
/// Opening must not acquire or upload audio until Activate. Activate is a nonblocking atomic gate
/// operation and must not invoke external callbacks or wait on devices, a dispatcher, or I/O.
/// Stop must prevent new acquisition and
/// enqueue before completion. A lease returned after cancellation is stopped and disposed without
/// activation. Implementations must dispose partial resources if opening itself fails.
/// </remarks>
public interface ICaptureLease : IAsyncDisposable
{
    void Activate();
    Task StopAsync(CaptureStopReason reason, CancellationToken cancellationToken);
}

/// <summary>Effects owned by the composition boundary, never by UI or device callbacks.</summary>
/// <remarks>
/// Async effect invocations must return their tasks promptly without blocking the calling thread.
/// PersistStart must commit session identity and immutable configured settings before capture opens;
/// the implementation owns the prevalidated configuration snapshot. A noncooperative durable start
/// can delay End because its outcome must be resolved before finalization. OpenCapture must return
/// only after all required sources are ready, using fresh epochs and ending any pause interval.
/// FenceCapture is a nonblocking,
/// thread-safe atomic gate that immediately prevents acquisition and new uploads and discards
/// unsent audio for Pause. A source's first cutoff for an epoch cannot be widened by a later Pause
/// or End; resume uses a new source epoch. It must not wait on I/O or invoke external callbacks. Recovery fencing
/// must also prevent further background writes. Pause must stop uploads and
/// dispose unsent frames, recording the cutoff. Only finals already sent at or before the source
/// cutoff may later commit. Reopening capture uses new source epochs. StopAndDrain must stop new
/// uploads, drain no longer than the supplied deadline, close or fence late provider results,
/// and explicitly return an incomplete result on timeout. FinalizeAndEnqueue atomically commits
/// the terminal state, completeness and one idempotent summary job. Its invocation must only
/// enqueue work without blocking or invoking callbacks; the returned task reports the durable
/// result. Recovery fencing also prevents a previously enqueued finalization from committing.
/// A failure or ambiguous commit
/// must throw. ClearDerivedContext clears only derived same-session memory, never saved history.
/// StopForRecovery stops acquisition/uploads and prevents further writes; retained text is unsaved.
/// ClearDerivedContext must respect the supplied epoch and must not apply an older reset after a
/// newer one. Generation cannot start until the current reset finishes.
/// These obligations require real implementations and are not proof of adapter integration.
/// </remarks>
public interface ISessionLifecycleEffects
{
    Task PersistStartAsync(SessionId session, CancellationToken cancellationToken);
    Task<ICaptureLease> OpenCaptureAsync(SessionId session, CancellationToken cancellationToken);
    void FenceCapture(SessionId session, CaptureStopReason reason, TimelinePosition cutoff);
    Task PauseAsync(SessionId session, TimelinePosition cutoff, CancellationToken cancellationToken);
    Task<DrainOutcome> StopAndDrainAsync(SessionId session, TimelinePosition cutoff, TimeSpan deadline, CancellationToken cancellationToken);
    Task FinalizeAndEnqueueAsync(SessionId session, DrainOutcome outcome, CancellationToken cancellationToken);
    Task ClearDerivedContextAsync(SessionId session, ContextEpoch epoch, TimelinePosition boundary, CancellationToken cancellationToken);
    Task HideAsync(SessionId session, CancellationToken cancellationToken);
    Task StopForRecoveryAsync(SessionId session, CancellationToken cancellationToken);
    Task MarkInterruptedAsync(SessionId session, CancellationToken cancellationToken);
}

/// <summary>The caller disposes the permit after its generation operation has stopped.</summary>
public sealed class GenerationPermit : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    internal GenerationPermit(AnswerFence fence)
    {
        Fence = fence;
        CancellationToken = cancellation.Token;
    }
    public AnswerFence Fence { get; }
    public CancellationToken CancellationToken { get; }
    internal void Cancel()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* A callback cannot prevent fencing or stopping capture. */ }
    }
    public void Dispose() => cancellation.Dispose();
}
