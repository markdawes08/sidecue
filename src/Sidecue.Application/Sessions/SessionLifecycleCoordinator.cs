using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Application.Sessions;

/// <summary>Serializes lifecycle effects and fences capture and foreground generation.</summary>
/// <remarks>
/// The effect implementations own actual persistence, transport and device resources. This class
/// must run off the UI dispatcher. A coordinator owns one session and is not reused after End.
/// </remarks>
public sealed class SessionLifecycleCoordinator : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly object activation = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly SemaphoreSlim clears = new(1, 1);
    private readonly CancellationTokenSource admissionClosed = new();
    private readonly HashSet<RequestId> usedRequestIds = [];
    private const int MaximumRequestIds = 4096;
    private readonly SessionId session;
    private readonly ISessionClock clock;
    private readonly ISessionLifecycleEffects effects;
    private readonly TimeSpan drainTimeout;
    private LifecycleSnapshot snapshot;
    private ICaptureLease? capture;
    private GenerationPermit? generation;
    private Task contextReady = Task.CompletedTask;
    private CancellationTokenSource? openingCancellation;
    private Task<bool>? lateOpenCleanup;
    private Task<bool>? captureStopResult;
    private TaskCompletionSource? pauseCompletion;
    private TaskCompletionSource? endCompletion;
    private bool pauseRequested;
    private bool endRequested;
    private bool recoveryRequested;
    private bool disposed;

    public SessionLifecycleCoordinator(SessionId session, ISessionClock clock, ISessionLifecycleEffects effects, TimeSpan drainTimeout)
    {
        if (session.Value == Guid.Empty) throw new ArgumentException("A session identity is required.", nameof(session));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.effects = effects ?? throw new ArgumentNullException(nameof(effects));
        if (clock.Anchor.SessionId != session) throw new ArgumentException("The clock must belong to the session.", nameof(clock));
        if (drainTimeout <= TimeSpan.Zero || drainTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        this.session = session;
        this.drainTimeout = drainTimeout;
        snapshot = new LifecycleSnapshot(session, SessionLifecycleState.Idle, SessionCaptureState.Stopped,
            SessionGenerationState.Idle, new ContextEpoch(0), clock.Anchor.Origin, true, null, null);
    }

    public LifecycleSnapshot Snapshot { get { lock (sync) return snapshot; } }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (sync)
            {
                if (snapshot.State != SessionLifecycleState.Idle || endRequested || recoveryRequested) return;
                snapshot = snapshot with { State = SessionLifecycleState.Starting, Capture = SessionCaptureState.Connecting, Error = null };
            }
            // Resolve the durable write even if a pause or end arrives. A late ambiguous start
            // must never race an apparent successful finalization.
            try { await effects.PersistStartAsync(session, CancellationToken.None).ConfigureAwait(false); }
            catch { await EnterRecoveryAsync().ConfigureAwait(false); return; }
            lock (sync) { if (pauseRequested || endRequested || recoveryRequested) return; }
            await OpenAsync().ConfigureAwait(false);
        }
        finally { commands.Release(); }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (sync)
            {
                if (snapshot.State != SessionLifecycleState.Paused || endRequested || recoveryRequested) return;
                if (lateOpenCleanup is { IsCompleted: false })
                    throw new InvalidOperationException("A prior capture open is still being cleaned up.");
                if (lateOpenCleanup is { IsCompletedSuccessfully: true } && !lateOpenCleanup.Result)
                    throw new InvalidOperationException("A prior capture open could not be safely cleaned up.");
                pauseRequested = false;
                pauseCompletion = null;
                snapshot = snapshot with { State = SessionLifecycleState.Starting, Capture = SessionCaptureState.Connecting, Error = null };
            }
            await OpenAsync().ConfigureAwait(false);
        }
        finally { commands.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimelinePosition? cutoff = null;
        CancellationTokenSource? opening = null;
        TaskCompletionSource? completion = null;
        Task? previous = null;
        bool fenceFailed = false;
        lock (activation)
        {
            lock (sync)
            {
                ThrowIfDisposed();
                if (snapshot.State is not (SessionLifecycleState.Starting or SessionLifecycleState.Listening)) return;
                if (pauseRequested) previous = pauseCompletion?.Task;
                else
                {
                    cutoff = Position();
                    pauseRequested = true;
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    pauseCompletion = completion;
                    opening = openingCancellation;
                }
            }
            if (previous is null)
            {
                try { effects.FenceCapture(session, CaptureStopReason.Pause, cutoff!); }
                catch { fenceFailed = true; }
            }
        }
        if (previous is not null) { await previous.WaitAsync(cancellationToken).ConfigureAwait(false); return; }
        try
        {
            Cancel(opening);
            if (fenceFailed) { await EnterRecoveryAsync(EventErrorCategory.DeviceUnavailable).ConfigureAwait(false); return; }
            await commands.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (sync)
                {
                    if (endRequested || recoveryRequested || snapshot.State is SessionLifecycleState.Paused or SessionLifecycleState.Completed or SessionLifecycleState.Interrupted) return;
                }
                bool stopPending = false;
                try
                {
                    Task stop = StopCaptureAsync(CaptureStopReason.Pause);
                    try { await stop.WaitAsync(drainTimeout, CancellationToken.None).ConfigureAwait(false); }
                    catch (TimeoutException) { stopPending = true; _ = ObserveStopAsync(stop); throw; }
                    await effects.PauseAsync(session, cutoff!, CancellationToken.None).ConfigureAwait(false);
                    lock (sync)
                    {
                        if (!endRequested && !recoveryRequested)
                            snapshot = snapshot with { State = SessionLifecycleState.Paused, Capture = SessionCaptureState.Paused };
                    }
                }
                catch
                {
                    await EnterRecoveryAsync(EventErrorCategory.DeviceUnavailable).ConfigureAwait(false);
                    if (stopPending) lock (sync) snapshot = snapshot with { Capture = SessionCaptureState.Failed };
                }
            }
            finally { commands.Release(); }
        }
        finally { completion!.TrySetResult(); }
    }

    public async Task HideAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) { ThrowIfDisposed(); if (!snapshot.OverlayVisible) return; }
        await effects.HideAsync(session, cancellationToken).ConfigureAwait(false);
        lock (sync) snapshot = snapshot with { OverlayVisible = false };
    }

    public async Task ClearContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var boundary = Position();
        GenerationPermit? old;
        ContextEpoch epoch;
        TaskCompletionSource ready;
        lock (sync)
        {
            ThrowIfDisposed();
            if (endRequested || recoveryRequested || snapshot.State is SessionLifecycleState.Idle or SessionLifecycleState.Completed or SessionLifecycleState.Interrupted) return;
            if (boundary.Offset.Value < snapshot.ContextStart.Offset.Value) boundary = snapshot.ContextStart;
            epoch = new ContextEpoch(checked(snapshot.ContextEpoch.Value + 1));
            ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            contextReady = ready.Task;
            old = generation;
            generation = null;
            snapshot = snapshot with { ContextEpoch = epoch, ContextStart = boundary, ActiveRequest = null,
                Generation = old is null ? snapshot.Generation : SessionGenerationState.Cancelled };
        }
        old?.Cancel();
        await clears.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (sync) { if (snapshot.ContextEpoch != epoch || recoveryRequested || endRequested) return; }
            try { await effects.ClearDerivedContextAsync(session, epoch, boundary, CancellationToken.None).ConfigureAwait(false); }
            catch { await EnterRecoveryAsync(EventErrorCategory.Unknown).ConfigureAwait(false); }
        }
        finally { ready.TrySetResult(); clears.Release(); }
    }

    public async Task<GenerationPermit> BeginGenerationAsync(RequestId request, CancellationToken cancellationToken = default)
    {
        if (request.Value == Guid.Empty) throw new ArgumentException("A request identity is required.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, admissionClosed.Token);
        while (true)
        {
            Task ready;
            lock (sync)
            {
                ThrowIfDisposed();
                if (endRequested || recoveryRequested || snapshot.State is not (SessionLifecycleState.Listening or SessionLifecycleState.Paused))
                    throw new InvalidOperationException("The session is not accepting foreground requests.");
                ready = contextReady;
            }
            try
            {
                await ready.WaitAsync(admission.Token).ConfigureAwait(false);
                await commands.WaitAsync(admission.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (admissionClosed.IsCancellationRequested)
            {
                throw new InvalidOperationException("The session is not accepting foreground requests.");
            }
            try
            {
                GenerationPermit? previous;
                GenerationPermit next;
                lock (sync)
                {
                    ThrowIfDisposed();
                    if (endRequested || recoveryRequested || snapshot.State is not (SessionLifecycleState.Listening or SessionLifecycleState.Paused))
                        throw new InvalidOperationException("The session is not accepting foreground requests.");
                    if (!ReferenceEquals(ready, contextReady)) continue;
                    if (usedRequestIds.Contains(request))
                        throw new ArgumentException("A foreground request ID cannot be reused.", nameof(request));
                    if (usedRequestIds.Count >= MaximumRequestIds)
                        throw new InvalidOperationException("The session request identity limit was reached.");
                    usedRequestIds.Add(request);
                    previous = generation;
                    next = new GenerationPermit(new AnswerFence(session, snapshot.ContextEpoch, request));
                    generation = next;
                    snapshot = snapshot with { ActiveRequest = next.Fence, Generation = SessionGenerationState.BuildingContext };
                }
                previous?.Cancel();
                return next;
            }
            finally { commands.Release(); }
        }
    }

    public bool IsCurrent(AnswerFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        lock (sync) return !endRequested && !recoveryRequested && generation?.Fence == fence &&
            snapshot.Generation is SessionGenerationState.BuildingContext or SessionGenerationState.Streaming;
    }

    public bool MarkGenerationStreaming(AnswerFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        lock (sync)
        {
            if (endRequested || recoveryRequested || generation?.Fence != fence || snapshot.Generation != SessionGenerationState.BuildingContext) return false;
            snapshot = snapshot with { Generation = SessionGenerationState.Streaming };
            return true;
        }
    }

    public bool FailGeneration(AnswerFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        lock (sync)
        {
            if (endRequested || recoveryRequested || generation?.Fence != fence ||
                snapshot.Generation is not (SessionGenerationState.BuildingContext or SessionGenerationState.Streaming)) return false;
            generation = null;
            snapshot = snapshot with { ActiveRequest = null, Generation = SessionGenerationState.Failed };
            return true;
        }
    }

    public bool CompleteGeneration(AnswerFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        lock (sync)
        {
            if (endRequested || recoveryRequested || generation?.Fence != fence ||
                snapshot.Generation is not (SessionGenerationState.BuildingContext or SessionGenerationState.Streaming)) return false;
            generation = null;
            snapshot = snapshot with { ActiveRequest = null, Generation = SessionGenerationState.Completed };
            return true;
        }
    }

    public async Task EndAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimelinePosition? cutoff = null;
        CancellationTokenSource? opening = null;
        GenerationPermit? old = null;
        TaskCompletionSource? completion = null;
        Task? previous = null;
        bool fenceFailed = false;
        lock (activation)
        {
            lock (sync)
            {
                ThrowIfDisposed();
                if (snapshot.State is SessionLifecycleState.Idle or SessionLifecycleState.Completed or SessionLifecycleState.Interrupted or SessionLifecycleState.RecoveryRequired) return;
                if (endRequested) previous = endCompletion?.Task;
                else
                {
                    cutoff = Position();
                    endRequested = true;
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    endCompletion = completion;
                    opening = openingCancellation;
                    old = generation;
                    generation = null;
                    snapshot = snapshot with { State = SessionLifecycleState.Stopping, ActiveRequest = null,
                        Generation = old is null ? snapshot.Generation : SessionGenerationState.Cancelled };
                }
            }
            if (previous is null)
            {
                try { effects.FenceCapture(session, CaptureStopReason.End, cutoff!); }
                catch { fenceFailed = true; }
            }
        }
        if (previous is not null) { await previous.WaitAsync(cancellationToken).ConfigureAwait(false); return; }
        try
        {
            Cancel(admissionClosed);
            Cancel(opening);
            old?.Cancel();
            if (fenceFailed) { await EnterRecoveryAsync(EventErrorCategory.DeviceUnavailable).ConfigureAwait(false); return; }
            await commands.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (sync) { if (recoveryRequested || snapshot.State == SessionLifecycleState.Completed) return; }
                bool stopPending = false;
                EventErrorCategory failure = EventErrorCategory.DeviceUnavailable;
                try
                {
                    Task stop = StopCaptureAsync(CaptureStopReason.End);
                    try { await stop.WaitAsync(drainTimeout, CancellationToken.None).ConfigureAwait(false); }
                    catch (TimeoutException) { stopPending = true; _ = ObserveStopAsync(stop); throw; }
                    failure = EventErrorCategory.ProviderUnavailable;
                    using var drainCancellation = new CancellationTokenSource();
                    Task<DrainOutcome> draining = effects.StopAndDrainAsync(session, cutoff!, drainTimeout, drainCancellation.Token);
                    DrainOutcome outcome;
                    try { outcome = await draining.WaitAsync(drainTimeout, CancellationToken.None).ConfigureAwait(false); }
                    catch (TimeoutException) { Cancel(drainCancellation); _ = ObserveDrainAsync(draining); throw; }
                    failure = EventErrorCategory.StorageUnavailable;
                    Task finalization;
                    lock (activation)
                    {
                        lock (sync) { if (recoveryRequested) return; }
                        finalization = effects.FinalizeAndEnqueueAsync(session, outcome, CancellationToken.None);
                    }
                    await finalization.ConfigureAwait(false);
                    lock (sync)
                    {
                        if (!recoveryRequested) snapshot = snapshot with { State = SessionLifecycleState.Completed, Capture = SessionCaptureState.Stopped };
                    }
                }
                catch
                {
                    await EnterRecoveryAsync(failure).ConfigureAwait(false);
                    if (stopPending) lock (sync) snapshot = snapshot with { Capture = SessionCaptureState.Failed };
                }
            }
            finally { commands.Release(); }
        }
        finally { completion!.TrySetResult(); }
    }

    public Task ReportPersistenceFailureAsync(CancellationToken cancellationToken = default) =>
        EnterRecoveryCoreAsync(EventErrorCategory.StorageUnavailable, cancellationToken);

    private Task EnterRecoveryAsync(EventErrorCategory category = EventErrorCategory.StorageUnavailable) =>
        EnterRecoveryCoreAsync(category, CancellationToken.None);

    private async Task EnterRecoveryCoreAsync(EventErrorCategory category, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? opening;
        GenerationPermit? old;
        ICaptureLease? lease;
        Task<bool>? inFlightStop;
        bool fenced = true;
        lock (activation)
        {
            lock (sync)
            {
                ThrowIfDisposed();
                if (recoveryRequested) return;
                recoveryRequested = true;
                opening = openingCancellation;
                old = generation;
                generation = null;
                snapshot = snapshot with { State = SessionLifecycleState.RecoveryRequired, ActiveRequest = null,
                    Generation = old is null ? snapshot.Generation : SessionGenerationState.Cancelled, Error = category };
            }
            lease = Interlocked.Exchange(ref capture, null);
            inFlightStop = captureStopResult;
            try { effects.FenceCapture(session, CaptureStopReason.Recovery, Position()); }
            catch { fenced = false; }
        }
        Cancel(admissionClosed);
        Cancel(opening);
        old?.Cancel();
        Task effectStop;
        try { effectStop = effects.StopForRecoveryAsync(session, CancellationToken.None); }
        catch { effectStop = Task.FromException(new InvalidOperationException("Recovery stop failed.")); }
        bool stopped = fenced;
        try
        {
            Task leaseStop = lease is null ? Task.CompletedTask : StopLeaseAsync(lease, CaptureStopReason.Recovery);
            Task all = Task.WhenAll(effectStop, leaseStop, inFlightStop ?? Task.FromResult(true));
            try { await all.WaitAsync(drainTimeout, CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { _ = ObserveStopAsync(all); throw; }
            if (inFlightStop is not null && !await inFlightStop.ConfigureAwait(false)) stopped = false;
        }
        catch { stopped = false; }
        lock (sync) snapshot = snapshot with { Capture = stopped ? SessionCaptureState.Stopped : SessionCaptureState.Failed };
    }

    public async Task MarkInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Snapshot.State != SessionLifecycleState.Idle) return;
            try
            {
                await effects.MarkInterruptedAsync(session, CancellationToken.None).ConfigureAwait(false);
                lock (sync)
                {
                    if (!recoveryRequested)
                        snapshot = snapshot with { State = SessionLifecycleState.Interrupted, Capture = SessionCaptureState.Stopped };
                }
            }
            catch { await EnterRecoveryAsync().ConfigureAwait(false); }
        }
        finally { commands.Release(); }
    }

    private async Task OpenAsync()
    {
        using var opening = new CancellationTokenSource();
        lock (sync)
        {
            if (pauseRequested || endRequested || recoveryRequested) return;
            openingCancellation = opening;
        }
        Task<ICaptureLease>? task = null;
        try
        {
            task = effects.OpenCaptureAsync(session, opening.Token);
            ICaptureLease opened = await task.WaitAsync(opening.Token).ConfigureAwait(false);
            bool stop;
            lock (activation)
            {
                lock (sync) stop = pauseRequested || endRequested || recoveryRequested;
                if (!stop)
                {
                    capture = opened;
                    opened.Activate();
                    lock (sync) snapshot = snapshot with { State = SessionLifecycleState.Listening, Capture = SessionCaptureState.Live };
                }
            }
            if (stop)
            {
                try { await StopLeaseAsync(opened, CaptureStopReason.Pause).ConfigureAwait(false); }
                catch
                {
                    await EnterRecoveryAsync(EventErrorCategory.DeviceUnavailable).ConfigureAwait(false);
                    lock (sync) snapshot = snapshot with { Capture = SessionCaptureState.Failed };
                }
                return;
            }
        }
        catch (OperationCanceledException) when (opening.IsCancellationRequested)
        {
            if (task is not null) lateOpenCleanup = CleanupLateOpenAsync(task);
        }
        catch
        {
            bool cleanupFailed = false;
            if (capture is not null)
            {
                try { await StopCaptureAsync(CaptureStopReason.Recovery).ConfigureAwait(false); } catch { cleanupFailed = true; }
            }
            if (cleanupFailed)
            {
                await EnterRecoveryAsync(EventErrorCategory.DeviceUnavailable).ConfigureAwait(false);
                lock (sync) snapshot = snapshot with { Capture = SessionCaptureState.Failed };
                return;
            }
            lock (sync)
            {
                if (!pauseRequested && !endRequested && !recoveryRequested)
                    snapshot = snapshot with { State = SessionLifecycleState.Interrupted, Capture = SessionCaptureState.Stopped, Error = EventErrorCategory.DeviceUnavailable };
            }
        }
        finally
        {
            lock (sync) { if (ReferenceEquals(openingCancellation, opening)) openingCancellation = null; }
        }
    }

    private static async Task<bool> CleanupLateOpenAsync(Task<ICaptureLease> task)
    {
        ICaptureLease lease;
        try { lease = await task.ConfigureAwait(false); }
        catch { return true; } // Opening owns and releases resources on failure.
        try { await StopLeaseAsync(lease, CaptureStopReason.Pause).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    private static async Task ObserveDrainAsync(Task<DrainOutcome> task)
    {
        try { _ = await task.ConfigureAwait(false); } catch { }
    }

    private static async Task ObserveStopAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { }
    }

    private async Task StopCaptureAsync(CaptureStopReason reason)
    {
        ICaptureLease? current;
        TaskCompletionSource<bool>? result = null;
        lock (activation)
        {
            current = Interlocked.Exchange(ref capture, null);
            if (current is not null)
            {
                result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                captureStopResult = result.Task;
            }
        }
        if (current is null) return;
        try { await StopLeaseAsync(current, reason).ConfigureAwait(false); result!.TrySetResult(true); }
        catch { result!.TrySetResult(false); throw; }
    }

    private static async Task StopLeaseAsync(ICaptureLease lease, CaptureStopReason reason)
    {
        try { await lease.StopAsync(reason, CancellationToken.None).ConfigureAwait(false); }
        finally { await lease.DisposeAsync().ConfigureAwait(false); }
    }

    private TimelinePosition Position()
    {
        TimelinePosition position = clock.GetPosition();
        if (position.TimelineEpoch != clock.Anchor.Origin.TimelineEpoch || position.Offset.Value < clock.Anchor.Origin.Offset.Value)
            throw new InvalidOperationException("The session clock returned an invalid position.");
        return position;
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public async ValueTask DisposeAsync()
    {
        bool shouldStop;
        lock (sync) shouldStop = !disposed && snapshot.State is not (SessionLifecycleState.Idle or SessionLifecycleState.Completed or SessionLifecycleState.Interrupted or SessionLifecycleState.RecoveryRequired);
        if (shouldStop) await EnterRecoveryAsync(EventErrorCategory.Cancelled).ConfigureAwait(false);
        CancellationTokenSource? opening;
        GenerationPermit? old;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            opening = openingCancellation;
            old = generation;
            generation = null;
        }
        Cancel(admissionClosed);
        Cancel(opening);
        old?.Cancel();
        if (!await commands.WaitAsync(drainTimeout).ConfigureAwait(false))
        {
            // One outstanding operation can still hold the gate (for example, an
            // ambiguous durable start). Acquisition is already fenced. Cleanup is
            // deferred rather than claiming that pending work was cancelled.
            _ = CleanupWhenAvailableAsync();
            return;
        }
        try
        {
            try { await StopCaptureAsync(CaptureStopReason.Recovery).ConfigureAwait(false); }
            catch { lock (sync) snapshot = snapshot with { Capture = SessionCaptureState.Failed }; }
        }
        finally { commands.Release(); }
    }

    private async Task CleanupWhenAvailableAsync()
    {
        await commands.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await StopCaptureAsync(CaptureStopReason.Recovery).ConfigureAwait(false); }
            catch { lock (sync) snapshot = snapshot with { Capture = SessionCaptureState.Failed }; }
        }
        finally { commands.Release(); }
    }
}
